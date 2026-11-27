using System.Text.Json;

namespace PhoneBox.Agent;

internal sealed record ScreenViewer(string Device, string Widget, long Handle);
internal sealed record ScreenConnection(ScreenViewer Viewer, long Generation);
internal sealed record ScreenOpenResult(string State, ScreenConnection? Connection);

internal sealed class ScreenFeeds : IAsyncDisposable
{
    private readonly ConfigStore _config;
    private readonly ScreenLauncher _launcher;
    private readonly Func<long> _nowMs;
    private readonly object _gate = new();
    private long _nextGeneration = 0;
    private readonly Dictionary<string, ScreenConnection> _byDevice = new(StringComparer.Ordinal);
    private readonly Dictionary<long, ConnectionState> _byGeneration = new();
    private int _globalCount = 0;
    private const int GlobalCap = 2;

    private sealed class ConnectionState
    {
        public required ScreenLease Lease;
        public required ScreenOptions Options;
        public IScreenRun? Run;
        public Func<ScreenFrame, Task>? Emit;
        public long LastHelperHealthMs;
        public long LastInputMs;
        public int InputCount;
        public int RetryAttempt;
        public bool Closed;
        public bool RecoveryRetention;
        public long RecoveryDeadlineMs;
    }

    public ScreenFeeds(ConfigStore config, ScreenLauncher launcher, Func<long> nowMs)
    {
        _config = config;
        _launcher = launcher;
        _nowMs = nowMs;
    }

    public async Task<ScreenOpenResult> OpenAsync(ScreenViewer viewer, Func<ScreenFrame, Task> emit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(emit);

        var widget = PanelWidgets.Find(_config.Get(), viewer.Device, viewer.Widget, "screen");
        if (widget is null) return new ScreenOpenResult("denied", null);

        var options = ScreenOptions.From(widget);

        long gen;
        ScreenConnection conn;
        ScreenLease lease;

        lock (_gate)
        {
            if (_byDevice.TryGetValue(viewer.Device, out var existing))
            {
                IScreenRun? oldRun = null;
                if (existing.Viewer.Widget != viewer.Widget)
                    return new ScreenOpenResult("busy", null);
                if (_byGeneration.TryGetValue(existing.Generation, out var oldSt))
                {
                    oldSt.Closed = true;
                    oldRun = oldSt.Run;
                    _byGeneration.Remove(existing.Generation);
                }
                _byDevice.Remove(viewer.Device);
                _globalCount = Math.Max(0, _globalCount - 1);
                if (oldRun is not null) _ = oldRun.DisposeAsync();
            }
            if (_globalCount >= GlobalCap)
                return new ScreenOpenResult("busy", null);

            gen = ++_nextGeneration;
            lease = new ScreenLease(viewer.Widget, viewer.Handle, _nowMs());
            conn = new ScreenConnection(viewer, gen);
            var st = new ConnectionState
            {
                Lease = lease,
                Options = options,
                Emit = emit,
                LastHelperHealthMs = _nowMs(),
                LastInputMs = 0,
                InputCount = 0,
                RetryAttempt = 0,
                Closed = false,
            };
            _byDevice[viewer.Device] = conn;
            _byGeneration[gen] = st;
            _globalCount++;
        }

        IScreenRun? run = null;
        try
        {
            Func<ScreenFrame, Task> receive = async frame =>
            {
                lock (_gate)
                {
                    if (_byGeneration.TryGetValue(gen, out var s) && !s.Closed)
                        s.LastHelperHealthMs = _nowMs();
                }
                await emit(frame);
            };
            run = await _launcher(options, receive, ct);
        }
        catch
        {
            lock (_gate)
            {
                if (_byGeneration.TryGetValue(gen, out var s)) s.Closed = true;
                _byGeneration.Remove(gen);
                if (_byDevice.TryGetValue(viewer.Device, out var c) && c.Generation == gen)
                    _byDevice.Remove(viewer.Device);
                _globalCount = Math.Max(0, _globalCount - 1);
            }
            if (run is not null) try { await run.DisposeAsync(); } catch { }
            return new ScreenOpenResult("no helper", null);
        }

        lock (_gate)
        {
            if (!_byGeneration.TryGetValue(gen, out var st) || st.Closed)
            {
                _ = run.DisposeAsync();
                return new ScreenOpenResult("no helper", null);
            }
            if (!_byDevice.TryGetValue(viewer.Device, out var c) || c.Generation != gen)
            {
                st.Closed = true;
                _byGeneration.Remove(gen);
                _globalCount = Math.Max(0, _globalCount - 1);
                _ = run.DisposeAsync();
                return new ScreenOpenResult("no helper", null);
            }
            st.Run = run;
        }

        return new ScreenOpenResult("ready", conn);
    }

    public async Task CloseAsync(ScreenConnection connection)
    {
        if (connection is null) return;
        IScreenRun? run = null;
        lock (_gate)
        {
            if (!_byGeneration.TryGetValue(connection.Generation, out var st)) return;
            if (st.Closed) return;
            if (st.Lease.Viewer != connection.Viewer.Handle) return;
            st.Closed = true;
            run = st.Run;
            _byGeneration.Remove(connection.Generation);
            if (_byDevice.TryGetValue(connection.Viewer.Device, out var c) && c.Generation == connection.Generation)
                _byDevice.Remove(connection.Viewer.Device);
            _globalCount = Math.Max(0, _globalCount - 1);
        }
        if (run is not null) try { await run.DisposeAsync(); } catch { }
    }

    public Task HeartbeatAsync(ScreenConnection connection)
    {
        lock (_gate)
        {
            if (!_byGeneration.TryGetValue(connection.Generation, out var st) || st.Closed) return Task.CompletedTask;
            if (st.Lease.Viewer != connection.Viewer.Handle) return Task.CompletedTask;
            st.Lease.Heartbeat(connection.Viewer.Handle, _nowMs());
        }
        return Task.CompletedTask;
    }

    public Task SettingsSavedAsync()
    {
        List<ScreenConnection> toClose = new();
        List<string> devices;
        lock (_gate) { devices = _byDevice.Keys.ToList(); }
        foreach (var dev in devices)
        {
            ScreenConnection? conn;
            lock (_gate) { _byDevice.TryGetValue(dev, out conn); }
            if (conn is null) continue;
            lock (_gate)
            {
                if (!_byGeneration.TryGetValue(conn.Generation, out var st) || st.Closed) continue;
                var w = PanelWidgets.Find(_config.Get(), dev, conn.Viewer.Widget, "screen");
                if (w is null) { toClose.Add(conn); continue; }
                var newOpts = ScreenOptions.From(w);
                if (newOpts != st.Options) toClose.Add(conn);
            }
        }
        foreach (var c in toClose) _ = CloseAsync(c);
        return Task.CompletedTask;
    }

    public Task TickAsync()
    {
        var now = _nowMs();
        List<long> gens = new();
        lock (_gate)
        {
            foreach (var (gen, st) in _byGeneration)
            {
                if (st.Closed) continue;
                if (st.Lease.Expired(now) || now - st.LastHelperHealthMs >= 6000)
                    gens.Add(gen);
            }
        }
        foreach (var g in gens)
        {
            ScreenConnection? conn = null;
            lock (_gate)
            {
                if (_byGeneration.TryGetValue(g, out var st) && !st.Closed)
                {
                    foreach (var kv in _byDevice)
                    {
                        if (kv.Value.Generation == g) { conn = kv.Value; break; }
                    }
                }
            }
            if (conn is not null) _ = CloseAsync(conn);
        }
        return Task.CompletedTask;
    }

    public Task InputAsync(ScreenConnection connection, string json, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 4096) return Task.CompletedTask;
        ConnectionState? st;
        lock (_gate)
        {
            if (!_byGeneration.TryGetValue(connection.Generation, out st) || st is null || st.Closed) return Task.CompletedTask;
            if (st.Lease.Viewer != connection.Viewer.Handle) return Task.CompletedTask;
            if (!st.Options.Control) return Task.CompletedTask;
        }
        var now = _nowMs();
        lock (_gate)
        {
            if (st is null || st.Closed) return Task.CompletedTask;
            if (now - st.LastInputMs < 8)
            {
                st.InputCount++;
                if (st.InputCount > 5) return Task.CompletedTask;
            }
            else
            {
                st.LastInputMs = now;
                st.InputCount = 0;
            }
        }

        var w = PanelWidgets.Find(_config.Get(), connection.Viewer.Device, connection.Viewer.Widget, "screen");
        if (w is null)
        {
            _ = CloseAsync(connection);
            return Task.CompletedTask;
        }

        object? cmd;
        try
        {
            using var doc = JsonDocument.Parse(json);
            cmd = doc.RootElement.Clone();
        }
        catch
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            if (!_byGeneration.TryGetValue(connection.Generation, out st) || st is null || st.Closed) return Task.CompletedTask;
            if (st.Run is null) return Task.CompletedTask;
            _ = st.Run.SendAsync(cmd!, ct);
        }
        return Task.CompletedTask;
    }

    public static TimeSpan RetryDelay(int attempt) => attempt switch
    {
        0 => TimeSpan.FromSeconds(1),
        1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(4),
        3 => TimeSpan.FromSeconds(8),
        _ => TimeSpan.FromSeconds(30),
    };

    public Task<bool> PrepareRecoveryAsync(ScreenViewer viewer)
    {
        lock (_gate)
        {
            if (!_byDevice.TryGetValue(viewer.Device, out var conn)) return Task.FromResult(false);
            if (conn.Viewer.Handle != viewer.Handle) return Task.FromResult(false);
            if (!_byGeneration.TryGetValue(conn.Generation, out var st) || st.Closed) return Task.FromResult(false);
            if (st.Lease.Viewer != viewer.Handle) return Task.FromResult(false);
            st.RecoveryRetention = true;
            st.RecoveryDeadlineMs = _nowMs() + 10_000;
            return Task.FromResult(true);
        }
    }

    public Task ResetMediaAsync(ScreenViewer viewer)
    {
        lock (_gate)
        {
            if (_byDevice.TryGetValue(viewer.Device, out var conn) && conn.Viewer.Handle == viewer.Handle
                && _byGeneration.TryGetValue(conn.Generation, out var st))
            {
                st.RecoveryRetention = false;
            }
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        List<ScreenConnection> all;
        lock (_gate)
        {
            all = _byDevice.Values.ToList();
            _byDevice.Clear();
            _byGeneration.Clear();
            _globalCount = 0;
        }
        foreach (var c in all) await CloseAsync(c);
    }
}
