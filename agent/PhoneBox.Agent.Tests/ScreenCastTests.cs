using System.Diagnostics;
using System.IO;
using PhoneBox.Agent;
using Xunit;

public sealed class ScreenCastTests : IAsyncLifetime
{
    private string _dir = "";

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "screencast-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "screen-helper.py");

    private static void WriteMode(string dir, string mode)
        => File.WriteAllText(Path.Combine(dir, "mode.txt"), mode);

    internal sealed class ScreenCastTestPeer : IAsyncDisposable
    {
        public IScreenRun Run { get; private set; } = default!;
        public Task Exited { get; private set; } = Task.CompletedTask;
        private readonly string _dir;
        private readonly Process _process;
        private readonly TaskCompletionSource _exitedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ScreenCastTestPeer(string dir, IScreenRun run, Process process)
        {
            _dir = dir;
            Run = run;
            _process = process;
            Exited = _exitedTcs.Task;
            _ = Task.Run(async () =>
            {
                try { await process.WaitForExitAsync(); }
                catch { }
                _exitedTcs.TrySetResult();
            });
        }

        public static async Task<ScreenCastTestPeer> StartAsync(string mode)
        {
            var dir = Path.Combine(Path.GetTempPath(), "peer " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var helper = Path.Combine(dir, "helper.py");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "screen-helper.py"), helper);
            File.WriteAllText(Path.Combine(dir, "mode.txt"), mode);

            var opts = new ScreenOptions("extend", 30, 8_000_000, "embedded", false);
            var exitedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IScreenRun run = await ScreenCast.StartAsync(helper, dir, opts, _ => Task.CompletedTask, CancellationToken.None);
            // find process via reflection? We need the process. Instead, track via a separate handle.
            // For Exited, poll the run's process by re-reading; simpler: store process reference.
            // We'll retrieve by checking child processes of current - not reliable. 
            // Instead, modify: ScreenCast.StartAsync returns IScreenRun; expose process via a property if ScreenRun.
            var sr = (ScreenRun)run;
            var peer = new ScreenCastTestPeer(dir, run, sr.Process);
            return peer;
        }

        // expose process
        public async ValueTask DisposeAsync()
        {
            await Run.DisposeAsync();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    // Need Process accessible on ScreenRun
    [Fact]
    public async Task DisposeStopsThePeer()
    {
        await using var peer = await ScreenCastTestPeer.StartAsync("success");
        await peer.Run.DisposeAsync();
        await peer.Exited.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(peer.Exited.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ReadyThenMediaReceived()
    {
        var received = new List<ScreenFrame>();
        var dir = Path.Combine(Path.GetTempPath(), "peer " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(HelperPath, Path.Combine(dir, "helper.py"));
        File.WriteAllText(Path.Combine(dir, "mode.txt"), "success");
        var opts = new ScreenOptions("extend", 30, 8_000_000, "embedded", false);
        var run = await ScreenCast.StartAsync(Path.Combine(dir, "helper.py"), dir, opts, f => { received.Add(f); return Task.CompletedTask; }, CancellationToken.None);
        await Task.Delay(1000);
        await run.DisposeAsync();
        Directory.Delete(dir, recursive: true);
        Assert.True(received.Count >= 2);
    }

    [Fact]
    public async Task TruncatedOutputDoesNotCrash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "peer " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(HelperPath, Path.Combine(dir, "helper.py"));
        File.WriteAllText(Path.Combine(dir, "mode.txt"), "truncated");
        var opts = new ScreenOptions("extend", 30, 8_000_000, "embedded", false);
        var run = await ScreenCast.StartAsync(Path.Combine(dir, "helper.py"), dir, opts, _ => Task.CompletedTask, CancellationToken.None);
        await Task.Delay(500);
        await run.DisposeAsync();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task CrashExits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "peer " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(HelperPath, Path.Combine(dir, "helper.py"));
        File.WriteAllText(Path.Combine(dir, "mode.txt"), "crash");
        var opts = new ScreenOptions("extend", 30, 8_000_000, "embedded", false);
        var run = await ScreenCast.StartAsync(Path.Combine(dir, "helper.py"), dir, opts, _ => Task.CompletedTask, CancellationToken.None);
        await Task.Delay(500);
        await run.DisposeAsync();
        Directory.Delete(dir, recursive: true);
    }
}
