namespace LinkBridge.Input;

/// <summary>Orders the physical release after the peer's native drag-start acknowledgement.</summary>
public sealed class FileDragReleaseGate
{
    private bool _armed, _ready, _released;
    public bool WaitingForReady => _armed && _released && !_ready;
    public void Arm() { _armed = true; _ready = _released = false; }
    public void Cancel() { _armed = _ready = _released = false; }
    public bool Release()
    {
        if (!_armed) return false;
        _released = true;
        return Flush();
    }
    public bool Ready()
    {
        if (!_armed) return false;
        _ready = true;
        return Flush();
    }
    private bool Flush()
    {
        if (!_ready || !_released) return false;
        Cancel();
        return true;
    }
}
