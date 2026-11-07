using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace LinkBridge.App.Services;

/// <summary>Only native caption move loops can hand off; file OLE and content drags never enter.</summary>
public sealed class WindowsWindowEdge : IDisposable
{
    private const uint MoveStart = 0x000A, MoveEnd = 0x000B, LocationChanged = 0x800B;
    private readonly WinEventProc _callback;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _leftHeld;
    private readonly DispatcherTimer _poll;
    private nint _hook, _locationHook, _moving;
    private Rect _origin;
    private Placement _placement;
    private uint _movingProcess, _movingThread;
    private int _dragOffsetX, _dragOffsetY;
    private double _sourceScale = 1;
    private bool _cancelPending, _disposed, _probing, _heldEdgeObserved;
    private long _cancelDeadline;
    private long _candidateStarted, _lastProbe, _largestProbeGap;
    private int _timerProbes, _locationProbes;
    private readonly HashSet<nint> _borrowed = [];
    private readonly Dictionary<nint, SavedPlacement> _saved = [];
    private CaptionPress? _captionPress;
    private sealed record CaptionPress(nint Window, uint Process, Point Pointer, Rect Origin,
        Placement Placement, int OffsetX, int OffsetY, double Scale);

    public void ClearCaptionPress() => _captionPress = null;
    public void RecordCaptionPress(int x, int y)
    {
        _captionPress = null;
        if (_disposed || !_enabled()) return;
        var point = new Point { X = x, Y = y };
        var hwnd = GetAncestor(WindowFromPoint(point), 2);
        if (hwnd == 0 || _borrowed.Contains(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd) ||
            GetWindow(hwnd, 4) != 0 || (GetWindowLongPtrW(hwnd, -20).ToInt64() & (0x80L | 0x08000000L)) != 0) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId || !GetWindowRect(hwnd, out var origin)) return;
        var packed = (nint)((y & 0xffff) << 16 | (x & 0xffff));
        if (SendMessageTimeoutW(hwnd, 0x0084, 0, packed, 2, 40, out var hit) == 0 || hit != 2) return;
        var frame = DwmGetWindowAttribute(hwnd, 9, out var visible, Marshal.SizeOf<Rect>()) >= 0 ? visible : origin;
        var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(hwnd, ref placement)) placement.Length = 0;
        var dpi = GetDpiForWindow(hwnd);
        _captionPress = new(hwnd, pid, point, origin, placement,
            Math.Clamp(x - frame.Left, 0, Math.Max(0, frame.Right - frame.Left - 1)),
            Math.Clamp(y - frame.Top, 0, Math.Max(0, frame.Bottom - frame.Top - 1)), dpi == 0 ? 1 : dpi / 96.0);
        EmitDiagnostic("caption-press");
    }

    /// <summary>
    /// Runs on the creating UI dispatcher after local capture ends, while physical left is held.
    /// Offsets are physical pixels in the original captured DWM frame. The handler must call
    /// BeginWindowDrag synchronously before starting asynchronous capture/network work.
    /// </summary>
    public event Action<nint, string, int, int, double>? HandoffRequested;
    /// <summary>Fixed phase/reason diagnostics only; never includes source captions, paths, or pairing data.</summary>
    public event Action<string>? Diagnostic;
    public WindowsWindowEdge(Func<bool> enabled, Func<bool>? leftHeld = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _enabled = enabled; _leftHeld = leftHeld ?? (() => (GetAsyncKeyState(1) & 0x8000) != 0); _callback = OnEvent;
        // Background-priority ticks can be postponed by continuous native input/render work.
        // This is a fallback: native source-location events also probe while caption movement runs.
        _poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _poll.Tick += (_, _) => PollDrag(ProbeSource.Timer);
        _hook = SetWinEventHook(MoveStart, MoveEnd, 0, _callback, 0, 0, 2); // Out-of-context, skip own process.
        if (_hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _locationHook = SetWinEventHook(LocationChanged, LocationChanged, 0, _callback, 0, 0, 2);
    }
    private void OnEvent(nint hook, uint kind, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || hwnd == 0) return;
        if (kind == LocationChanged)
        {
            // A separate exact-range hook avoids subscribing to every accessibility event.
            // Child/content locations and unrelated windows cannot arm a handoff.
            if (objectId == 0 && childId == 0 && hwnd == _moving) PollDrag(ProbeSource.Location);
            return;
        }
        if (kind == MoveStart)
        {
            if (_cancelPending) return;
            ResetCandidate();
            if (!_enabled() || _borrowed.Contains(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd) ||
                !_leftHeld() || GetWindow(hwnd, 4) != 0 ||
                (GetWindowLongPtrW(hwnd, -20).ToInt64() & (0x80L | 0x08000000L)) != 0 ||
                !GetCursorPos(out var point) || !GetWindowRect(hwnd, out var rectangle)) return;
            // WinEvent delivery is asynchronous: the cursor may have left the old
            // caption by this point. Prefer the hit recorded on the actual press.
            var press = _captionPress;
            GetWindowThreadProcessId(hwnd, out var candidatePid);
            var recordedCaption = press?.Window == hwnd && press.Process == candidatePid;
            var coordinates = (nint)((point.Y & 0xffff) << 16 | (point.X & 0xffff));
            if (!recordedCaption && (SendMessageTimeoutW(hwnd, 0x0084, 0, coordinates, 2, 80, out var hit) == 0 || hit != 2))
            { EmitDiagnostic("cancel", "move-start-caption-hit-missed"); return; }
            _movingThread = GetWindowThreadProcessId(hwnd, out _movingProcess);
            if (_movingProcess == Environment.ProcessId || _movingThread == 0) return;
            _moving = hwnd; _origin = rectangle;
            _placement = new Placement { Length = Marshal.SizeOf<Placement>() };
            if (!GetWindowPlacement(hwnd, ref _placement)) _placement.Length = 0;
            var frame = DwmGetWindowAttribute(hwnd, 9, out var visible, Marshal.SizeOf<Rect>()) >= 0 ? visible : rectangle;
            _dragOffsetX = Math.Clamp(point.X - frame.Left, 0, Math.Max(0, frame.Right - frame.Left - 1));
            _dragOffsetY = Math.Clamp(point.Y - frame.Top, 0, Math.Max(0, frame.Bottom - frame.Top - 1));
            var dpi = GetDpiForWindow(hwnd); _sourceScale = dpi == 0 ? 1 : dpi / 96.0;
            if (recordedCaption)
            {
                _origin = press!.Origin; _placement = press.Placement;
                _dragOffsetX = press.OffsetX; _dragOffsetY = press.OffsetY; _sourceScale = press.Scale;
            }
            _candidateStarted = _lastProbe = Environment.TickCount64; _largestProbeGap = 0;
            _timerProbes = _locationProbes = 0; _heldEdgeObserved = false;
            EmitDiagnostic("caption-start", "press-cached=" + recordedCaption + " · location-hook=" + (_locationHook != 0 ? "ready" : "unavailable"));
            _poll.Start();
            PollDrag(ProbeSource.Start); // Never wait for the first timer tick if already at the edge.
        }
        else if (kind == MoveEnd && hwnd == _moving)
        {
            if (!_cancelPending)
            { EmitTerminal("cancel", "local-move-ended"); ResetCandidate(); } // Mouse-up never starts a handoff.
        }
    }
    private enum ProbeSource { Start, Timer, Location }
    private void PollDrag(ProbeSource source)
    {
        if (_probing) return; // Cross-thread SendMessage can pump native callbacks reentrantly.
        _probing = true;
        try { ProbeDrag(source); } finally { _probing = false; }
    }
    private void ProbeDrag(ProbeSource source)
    {
        if (_disposed || _moving == 0) { ResetCandidate(); return; }
        var now = Environment.TickCount64;
        _largestProbeGap = Math.Max(_largestProbeGap, now - _lastProbe); _lastProbe = now;
        if (source == ProbeSource.Timer) _timerProbes++;
        else if (source == ProbeSource.Location) _locationProbes++;
        var hwnd = _moving;
        GetWindowThreadProcessId(hwnd, out var pid);
        var enabled = _enabled();
        var held = _leftHeld();
        var validWindow = IsWindow(hwnd);
        var sameProcess = pid == _movingProcess;
        var havePointer = GetCursorPos(out var point);
        var atEdge = havePointer && IsPrimaryRightEdge(point);
        if (held && atEdge) _heldEdgeObserved = true;
        var releasedAfterEdge = _cancelPending && _heldEdgeObserved && atEdge && now <= _cancelDeadline;
        if (!enabled || !validWindow || !sameProcess || (!held && !releasedAfterEdge))
        {
            var reason = !validWindow ? "source-window-gone" : !sameProcess ? "source-window-process-changed"
                : !enabled ? "feature-guard-disabled" : "physical-left-released";
            EmitTerminal("cancel", reason, held, enabled, atEdge);
            if (_cancelPending) Restore(hwnd);
            ResetCandidate(); return;
        }
        if (!_cancelPending)
        {
            if (!atEdge) return;
            _saved[hwnd] = new(_movingProcess, _origin, _placement);
            _cancelPending = true; _cancelDeadline = Environment.TickCount64 + 500;
            EmitDiagnostic("edge-armed");
            // End native move/capture without manufacturing a physical mouse-up.
            // The held state transfers independently of Finder/file-drag semantics.
            if (SendMessageTimeoutW(hwnd, 0x001F, 0, 0, 2, 100, out _) == 0)
            { EmitTerminal("failure", "cancel-mode-not-delivered"); Restore(hwnd); ResetCandidate(); return; }
        }
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var released = GetGUIThreadInfo(_movingThread, ref info) && info.Capture == 0 && (info.Flags & 2) == 0;
        if (!released)
        {
            if (Environment.TickCount64 >= _cancelDeadline)
            { EmitTerminal("failure", "native-capture-did-not-end"); Restore(hwnd); ResetCandidate(); }
            return;
        }
        // Keep a complete source render backing, including its Windows chrome. Retain the saved
        // placement until return/disconnect, so resizing can also be rolled back.
        EmitDiagnostic("cancel", "native-capture-ended");
        if (!RestorePlacement(hwnd, _saved[hwnd]))
        { EmitTerminal("failure", "source-placement-not-restored"); Restore(hwnd); ResetCandidate(); return; }
        // Original placement can already be partly off-screen after a previous drag. Move only
        // the live render backing into the primary work area; retain the saved user's placement
        // unchanged so return/disconnect can restore it. No source size or activation changes.
        if (!ClampBackingToPrimary(hwnd, out var failure))
        { EmitTerminal("failure", failure); Restore(hwnd); ResetCandidate(); return; }
        var offsetX = _dragOffsetX; var offsetY = _dragOffsetY; var scale = _sourceScale;
        var text = new System.Text.StringBuilder(513); GetWindowTextW(hwnd, text, text.Capacity);
        ResetCandidate();
        EmitTerminal("handoff", "requested");
        try { HandoffRequested?.Invoke(hwnd, text.ToString(), offsetX, offsetY, scale); }
        catch { EmitDiagnostic("failure", "handoff-handler-failed"); Restore(hwnd); throw; }
    }

    private static bool IsPrimaryRightEdge(Point point) => point.X >= GetSystemMetrics(0) - 8 &&
        point.Y >= 0 && point.Y < GetSystemMetrics(1);

    private void EmitTerminal(string phase, string reason, bool? held = null, bool? enabled = null, bool? atEdge = null)
    {
        var now = Environment.TickCount64;
        var currentHeld = held ?? _leftHeld();
        var currentEnabled = enabled ?? _enabled();
        var currentEdge = atEdge ?? (GetCursorPos(out var point) && IsPrimaryRightEdge(point));
        // One terminal snapshot per candidate: no pointer trajectory, captions, identifiers or per-frame logs.
        var gap = Math.Max(_largestProbeGap, now - _lastProbe);
        EmitDiagnostic(phase, reason + " · held=" + currentHeld + " · enabled=" + currentEnabled +
            " · edgeNow=" + currentEdge + " · heldEdgeObserved=" + _heldEdgeObserved +
            " · maxPollGapMs=" + gap + " · elapsedMs=" + (now - _candidateStarted) +
            " · timerProbes=" + _timerProbes + " · locationProbes=" + _locationProbes);
    }

    private void EmitDiagnostic(string phase, string? reason = null)
    {
        try { Diagnostic?.Invoke("窗口边缘 · " + phase + (reason is null ? "" : " · " + reason)); }
        catch { } // A diagnostic subscriber must not break native event/capture cleanup.
    }

    private static bool ClampBackingToPrimary(nint hwnd, out string failure)
    {
        failure = "source-backing-unavailable";
        if (!GetWindowRect(hwnd, out var outer) ||
            DwmGetWindowAttribute(hwnd, 9, out var frame, Marshal.SizeOf<Rect>()) < 0) return false;
        var width = frame.Right - frame.Left; var height = frame.Bottom - frame.Top;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(MonitorFromPoint(new Point(), 1), ref monitor)) return false;
        var area = monitor.Work;
        if (width <= 0 || height <= 0 || area.Right <= area.Left || area.Bottom <= area.Top) return false;
        if (width > area.Right - area.Left || height > area.Bottom - area.Top)
        { failure = "source-frame-exceeds-primary-work-area"; return false; }
        var left = Math.Clamp(frame.Left, area.Left, area.Right - width);
        var top = Math.Clamp(frame.Top, area.Top, area.Bottom - height);
        if (left == frame.Left && top == frame.Top) return true;
        // GetWindowRect includes invisible resize margins; DWM/WGC frame coordinates do not.
        // Preserve their physical inset while translating, and verify native placement rather
        // than assuming DWM's compositor metadata has refreshed in the same call.
        var insetX = frame.Left - outer.Left; var insetY = frame.Top - outer.Top;
        if (!SetWindowPos(hwnd, 0, left - insetX, top - insetY, 0, 0, 0x0015) ||
            !GetWindowRect(hwnd, out var placed)) return false; // NOSIZE | NOZORDER | NOACTIVATE
        var placedLeft = placed.Left + insetX; var placedTop = placed.Top + insetY;
        if (placedLeft < area.Left || placedTop < area.Top ||
            placedLeft + width > area.Right || placedTop + height > area.Bottom)
        { failure = "source-backing-remains-outside-primary-work-area"; return false; }
        return true;
    }
    private void ResetCandidate()
    { _poll.Stop(); _moving = 0; _cancelPending = false; }

    /// <summary>Restore only if the saved HWND still belongs to its original process.</summary>
    public void Restore(nint hwnd)
    {
        if (_saved.Remove(hwnd, out var saved)) RestorePlacement(hwnd, saved);
        _borrowed.Remove(hwnd);
    }

    /// <summary>
    /// After LeaveRemote placed the real pointer back on Windows, restore and put the caption
    /// beneath that pointer, then post a native move start while physical left is still held.
    /// Only previously borrowed HWND/PID pairs can resume. This never sends mouse down/up.
    /// </summary>
    public bool ResumeDrag(nint hwnd, int offsetX, int offsetY)
    {
        if (_disposed || !_saved.TryGetValue(hwnd, out var saved) || !_leftHeld() ||
            !GetCursorPos(out var point) || !RestorePlacement(hwnd, saved) ||
            !GetWindowRect(hwnd, out var outer) ||
            DwmGetWindowAttribute(hwnd, 9, out var frame, Marshal.SizeOf<Rect>()) < 0 ||
            offsetX < 0 || offsetY < 0 || offsetX >= frame.Right - frame.Left || offsetY >= frame.Bottom - frame.Top ||
            WindowsWindowInput.HitTest(hwnd, offsetX, offsetY) != 2) return false;
        // Respect focus policy and fail closed rather than delivering a held gesture to another app.
        if (!SetForegroundWindow(hwnd) || GetAncestor(GetForegroundWindow(), 2) != hwnd) return false;
        var x = point.X - offsetX + outer.Left - frame.Left;
        var y = point.Y - offsetY + outer.Top - frame.Top;
        if (!SetWindowPos(hwnd, 0, x, y, 0, 0, 0x0015)) return false; // NOSIZE | NOZORDER | NOACTIVATE
        if (point.X is < short.MinValue or > short.MaxValue || point.Y is < short.MinValue or > short.MaxValue)
        { RestorePlacement(hwnd, saved); return false; }
        var packed = (nint)((point.Y & 0xffff) << 16 | (point.X & 0xffff));
        if (!PostMessageW(hwnd, 0x00A1, 2, packed)) // WM_NCLBUTTONDOWN / HTCAPTION; asynchronous native loop.
        { RestorePlacement(hwnd, saved); return false; }
        _saved.Remove(hwnd); _borrowed.Remove(hwnd);
        return true;
    }
    private static bool RestorePlacement(nint hwnd, SavedPlacement saved)
    {
        if (!IsWindow(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid); if (pid != saved.ProcessId) return false;
        var placement = saved.Placement;
        if (placement.Length != 0 && SetWindowPlacement(hwnd, ref placement)) return true;
        var origin = saved.Origin;
        return SetWindowPos(hwnd, 0, origin.Left, origin.Top, origin.Right - origin.Left,
            origin.Bottom - origin.Top, 0x0014); // NOZORDER | NOACTIVATE
    }
    private sealed record SavedPlacement(uint ProcessId, Rect Origin, Placement Placement);
    public void MarkBorrowed(nint hwnd, bool value) { if (value) _borrowed.Add(hwnd); else _borrowed.Remove(hwnd); }
    public void Dispose()
    {
        _disposed = true; ResetCandidate();
        var hook = Interlocked.Exchange(ref _hook, 0); if (hook != 0) UnhookWinEvent(hook);
        var location = Interlocked.Exchange(ref _locationHook, 0); if (location != 0) UnhookWinEvent(location);
        foreach (var hwnd in _saved.Keys.ToArray()) Restore(hwnd);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    { public int Length, Flags, ShowCommand; public Point Minimum, Maximum; public Rect NormalPosition; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    { public int Size; public uint Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate void WinEventProc(nint hook, uint kind, nint hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out Rect rectangle, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint hwnd, ref Placement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(nint hwnd, ref Placement placement);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint w, nint l, uint flags, uint timeout, out nuint result);
}
