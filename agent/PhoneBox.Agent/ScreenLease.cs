namespace PhoneBox.Agent;

/// <summary>
/// Ownership of one phone's screen session. Viewer heartbeat is the only expiry signal: an idle desktop
/// that sends no video keeps the lease alive as long as the viewer keeps heartbeating.
/// </summary>
internal sealed class ScreenLease
{
    private readonly object _gate = new();
    private readonly string _widgetKey;
    private long _viewer;
    private long _lastHeartbeatMs;
    private uint _epoch;
    private bool _closed;

    public const long HeartbeatTimeoutMs = 10_000;

    public ScreenLease(string widgetKey, long viewer, long nowMs)
    {
        _widgetKey = widgetKey ?? "";
        _viewer = viewer;
        _lastHeartbeatMs = nowMs;
        _epoch = 0;
    }

    public string WidgetKey { get { lock (_gate) return _widgetKey; } }
    public long Viewer { get { lock (_gate) return _viewer; } }
    public uint Epoch { get { lock (_gate) return _epoch; } }

    public bool Heartbeat(long viewer, long nowMs)
    {
        lock (_gate)
        {
            if (_closed || viewer != _viewer) return false;
            _lastHeartbeatMs = nowMs;
            return true;
        }
    }

    public bool Expired(long nowMs)
    {
        lock (_gate)
        {
            if (_closed) return true;
            return nowMs - _lastHeartbeatMs >= HeartbeatTimeoutMs;
        }
    }

    public bool Replace(long expectedViewer, long replacementViewer, long nowMs)
    {
        lock (_gate)
        {
            if (_closed || expectedViewer != _viewer) return false;
            _viewer = replacementViewer;
            _epoch++;
            _lastHeartbeatMs = nowMs;
            return true;
        }
    }

    public bool Close(long viewer)
    {
        lock (_gate)
        {
            if (_closed || viewer != _viewer) return false;
            _closed = true;
            return true;
        }
    }
}
