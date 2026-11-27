using System.Net.WebSockets;
using System.Text.Json;

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
        if (string.IsNullOrWhiteSpace(widget) || !int.TryParse(viewerStr, out var viewer))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method))
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            try
            {
                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                var buf = new byte[4096];
                while (ws.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
                {
                    var res = await ws.ReceiveAsync(buf, context.RequestAborted);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method))
        {
            // body cap 1 KiB
            if (context.Request.ContentLength is > 1024)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            using var reader = new StreamReader(context.Request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            if (body.Length > 1024) { context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
                if (!root.TryGetProperty("op", out var opEl) || opEl.GetString() != "recover") { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
                if (!root.TryGetProperty("viewer", out var vEl) || vEl.ValueKind != JsonValueKind.Number) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
                var v = vEl.GetInt32();
                if (v != viewer) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
                var sv = new ScreenViewer(device, widget, viewer);
                var ok = await feeds.PrepareRecoveryAsync(sv);
                context.Response.StatusCode = ok ? StatusCodes.Status204NoContent : StatusCodes.Status409Conflict;
            }
            catch (JsonException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
            return;
        }

        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
    }
}
