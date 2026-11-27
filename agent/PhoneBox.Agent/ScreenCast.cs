using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

internal interface IScreenRun : IAsyncDisposable
{
    Task SendAsync(object command, CancellationToken ct);
}

internal delegate Task<IScreenRun> ScreenLauncher(
    ScreenOptions options, Func<ScreenFrame, Task> receive, CancellationToken ct);

internal sealed class ScreenCast
{
    public static async Task<IScreenRun> StartAsync(
        string helperPath, string runtimeDirectory, ScreenOptions options,
        Func<ScreenFrame, Task> receive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(helperPath)) throw new ArgumentException("helper path required", nameof(helperPath));
        if (string.IsNullOrWhiteSpace(runtimeDirectory)) throw new ArgumentException("runtime directory required", nameof(runtimeDirectory));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(receive);

        var runId = Guid.NewGuid().ToString("N");
        var runtimeDir = Path.Combine(runtimeDirectory, runId);
        Directory.CreateDirectory(runtimeDir);
        try
        {
            // 0700
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                chmod(runtimeDir, 0x1C0);
            }
            var socketPath = Path.Combine(runtimeDir, "sock");

            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(socketPath);
            listener.Bind(endpoint);
            listener.Listen(1);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                chmod(socketPath, 0x180);
            }

            // launch helper
            var psi = new ProcessStartInfo
            {
                FileName = helperPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? "python3" : helperPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                CreateNoWindow = true,
            };
            if (helperPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add(helperPath);
            }
            psi.ArgumentList.Add("--socket");
            psi.ArgumentList.Add(socketPath);
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();

            // accept connection with timeout
            using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            acceptCts.CancelAfter(TimeSpan.FromSeconds(10));
            var acceptTask = listener.AcceptAsync(acceptCts.Token);
            Socket client;
            try
            {
                client = await acceptTask;
            }
            catch
            {
                TryKill(process);
                listener.Dispose();
                throw;
            }

            var run = new ScreenRun(runtimeDir, socketPath, listener, client, process, receive);
            return run;
        }
        catch
        {
            try { Directory.Delete(runtimeDir, recursive: true); } catch { }
            throw;
        }
    }

    private static void chmod(string path, int mode)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            UnixFileMode m = 0;
            if ((mode & 0x100) != 0) m |= UnixFileMode.UserRead;
            if ((mode & 0x080) != 0) m |= UnixFileMode.UserWrite;
            if ((mode & 0x040) != 0) m |= UnixFileMode.UserExecute;
            if ((mode & 0x020) != 0) m |= UnixFileMode.GroupRead;
            if ((mode & 0x010) != 0) m |= UnixFileMode.GroupWrite;
            if ((mode & 0x008) != 0) m |= UnixFileMode.GroupExecute;
            if ((mode & 0x004) != 0) m |= UnixFileMode.OtherRead;
            if ((mode & 0x002) != 0) m |= UnixFileMode.OtherWrite;
            if ((mode & 0x001) != 0) m |= UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, m);
        }
        catch { }
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}

internal sealed class ScreenRun : IScreenRun
{
    private readonly string _runtimeDir;
    private readonly string _socketPath;
    private readonly Socket _listener;
    private readonly Socket _client;
    private readonly Process _process;
    public Process Process => _process;
    private readonly Func<ScreenFrame, Task> _receive;
    private readonly CancellationTokenSource _readCts = new();
    private Task? _readTask;
    private bool _disposed;

    public ScreenRun(string runtimeDir, string socketPath, Socket listener, Socket client, Process process, Func<ScreenFrame, Task> receive)
    {
        _runtimeDir = runtimeDir;
        _socketPath = socketPath;
        _listener = listener;
        _client = client;
        _process = process;
        _receive = receive;
        _readTask = Task.Run(() => ReadLoop(client, receive, _readCts.Token));
    }

    private async Task ReadLoop(Socket client, Func<ScreenFrame, Task> receive, CancellationToken ct)
    {
        var stream = new NetworkStream(client, ownsSocket: false);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await ScreenProtocol.ReadAsync(stream, ct);
                if (frame is null) break; // clean EOF
                await receive(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally
        {
            try { client.Shutdown(SocketShutdown.Both); } catch { }
        }
    }

    public Task SendAsync(object command, CancellationToken ct)
    {
        // commands go over the same socket as framed JSON (kind 1)
        var json = JsonSerializer.Serialize(command);
        var bytes = Encoding.UTF8.GetBytes(json);
        var frame = new ScreenFrame(ScreenProtocol.KindJson, bytes);
        // write to client socket
        var stream = new NetworkStream(_client, ownsSocket: false);
        return ScreenProtocol.WriteAsync(stream, frame, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _readCts.Cancel();
        try { _client.Shutdown(SocketShutdown.Both); } catch { }
        try { _client.Dispose(); } catch { }
        try { _listener.Dispose(); } catch { }

        // graceful shutdown: wait up to 2s
        var exited = _process.WaitForExit(2000);
        if (!exited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            _process.WaitForExit(2000);
        }
        try { _process.Dispose(); } catch { }

        try { Directory.Delete(_runtimeDir, recursive: true); } catch { }
    }
}
