using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PhoneBox.Agent;

public static class ScreenEndpoint
{
    public static async Task HandleAsync(HttpContext context)
    {
        var feeds = context.RequestServices?.GetService<ScreenFeeds>();
        if (feeds is null)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            context.Response.StatusCode = StatusCodes.Status501NotImplemented;
            return;
        }

        var device = context.Request.Headers["X-PhoneBox-Device"].ToString();
        if (string.IsNullOrWhiteSpace(device))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var widget = context.Request.Query["widget"].ToString();
        var viewerStr = context.Request.Query["viewer"].ToString();
        if (string.IsNullOrWhiteSpace(widget) || !long.TryParse(viewerStr, out var viewerHandle))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method))
        {
            await HandleRecover(context, feeds, device, widget, viewerHandle);
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        var channel = Channel.CreateBounded<ScreenFrame>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var wsWrite = new SemaphoreSlim(1, 1);
        var viewer = new ScreenViewer(device, widget, viewerHandle);
        ScreenConnection? conn = null;

        async Task Emit(ScreenFrame frame)
        {
            if (!channel.Writer.TryWrite(frame))
            {
                // drop oldest already happened; nothing else to do
            }
            await Task.CompletedTask;
        }

        try
        {
            var open = await feeds.OpenAsync(viewer, Emit, context.RequestAborted);
            conn = open.Connection;
            if (conn is null)
            {
                var err = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "error", code = open.State }));
                await wsWrite.WaitAsync(context.RequestAborted);
                try { await ws.SendAsync(err, WebSocketMessageType.Text, true, context.RequestAborted); }
                finally { wsWrite.Release(); }
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, open.State, CancellationToken.None);
                return;
            }

            var sendTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in channel.Reader.ReadAllAsync(context.RequestAborted))
                    {
                        var buf = new byte[5 + frame.Payload.Length];
                        buf[0] = frame.Kind;
                        buf[1] = (byte)((frame.Payload.Length >> 24) & 0xFF);
                        buf[2] = (byte)((frame.Payload.Length >> 16) & 0xFF);
                        buf[3] = (byte)((frame.Payload.Length >> 8) & 0xFF);
                        buf[4] = (byte)(frame.Payload.Length & 0xFF);
                        Buffer.BlockCopy(frame.Payload, 0, buf, 5, frame.Payload.Length);
                        await wsWrite.WaitAsync(context.RequestAborted);
                        try { await ws.SendAsync(buf, WebSocketMessageType.Binary, true, context.RequestAborted); }
                        finally { wsWrite.Release(); }
                    }
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            }, context.RequestAborted);

            var recvBuf = new byte[8192];
            while (ws.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                WebSocketReceiveResult res;
                using var ms = new MemoryStream();
                do
                {
                    res = await ws.ReceiveAsync(recvBuf, context.RequestAborted);
                    if (res.MessageType == WebSocketMessageType.Close) goto done;
                    if (ms.Length + res.Count > 64 * 1024) goto done;
                    ms.Write(recvBuf, 0, res.Count);
                } while (!res.EndOfMessage);

                if (res.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    HandleClientMessage(feeds, conn, text, context.RequestAborted);
                }
            }
        done:
            channel.Writer.TryComplete();
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            channel.Writer.TryComplete();
            if (conn is not null)
            {
                try { await feeds.CloseAsync(conn); } catch { }
            }
        }
    }

    private static void HandleClientMessage(ScreenFeeds feeds, ScreenConnection conn, string text, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String) return;
            var op = opEl.GetString();
            if (op == "heartbeat")
            {
                _ = feeds.HeartbeatAsync(conn);
                return;
            }
            // input ops: forward to helper via feeds
            _ = feeds.InputAsync(conn, text, ct);
        }
        catch { }
    }

    private static async Task HandleRecover(HttpContext context, ScreenFeeds feeds, string device, string widget, long viewerHandle)
    {
        if (context.Request.ContentLength is > 1024)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        if (body.Length > 1024) { context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            if (!root.TryGetProperty("op", out var opEl) || opEl.GetString() != "recover") { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            if (!root.TryGetProperty("viewer", out var vEl) || vEl.ValueKind != JsonValueKind.Number) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            var v = vEl.GetInt64();
            if (v != viewerHandle) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            var sv = new ScreenViewer(device, widget, viewerHandle);
            var ok = await feeds.PrepareRecoveryAsync(sv);
            if (ok) await feeds.ResetMediaAsync(sv);
            context.Response.StatusCode = ok ? StatusCodes.Status204NoContent : StatusCodes.Status409Conflict;
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
}
