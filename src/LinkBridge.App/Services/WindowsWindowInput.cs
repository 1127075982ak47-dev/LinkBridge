using System.ComponentModel;
using System.Runtime.InteropServices;
using LinkBridge.Core.WindowRoaming;
using LinkBridge.Input;

namespace LinkBridge.App.Services;

/// <summary>Input stays bound to one authenticated session/window/process, never an arbitrary foreground app.</summary>
public sealed class WindowsWindowInput : IDisposable
{
    private readonly Guid _sessionId;
    private readonly nint _hwnd;
    private readonly uint _pid;
    private readonly WindowsInputCapture? _capture;
    private readonly HashSet<int> _keys = [], _buttons = [];
    private sealed record Geometry(int Width, int Height);
    private Geometry? _geometry;
    private bool _disposed;
    public bool FocusUnavailable { get; private set; }
    public WindowsWindowInput(Guid sessionId, nint hwnd, WindowsInputCapture? capture = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (sessionId == Guid.Empty || !IsWindow(hwnd)) throw new ArgumentException("无效的窗口输入目标。");
        _sessionId = sessionId; _hwnd = hwnd; _capture = capture;
        GetWindowThreadProcessId(hwnd, out _pid);
    }
    public void UpdateFrameGeometry(int width, int height)
    { WindowRoamingLimits.FrameBytes(width, height); Volatile.Write(ref _geometry, new(width, height)); }
    public bool Apply(WindowRemoteInput input)
    {
        FocusUnavailable = false;
        if (_disposed || input.SessionId != _sessionId) return false;
        WindowRoamingLimits.Validate(input);
        if (input.Kind == "release") { ReleaseAll(); return true; }
        if (!IsWindow(_hwnd) || IsIconic(_hwnd)) { ReleaseAll(); return false; }
        GetWindowThreadProcessId(_hwnd, out var pid);
        if (pid != _pid) { ReleaseAll(); return false; }
        if (input.Kind == "resize") return Resize(input.Width, input.Height);
        if (input.Kind is not ("move" or "button" or "wheel" or "key" or "text")) return false;
        var pointer = input.Kind is "move" or "button" or "wheel";
        var geometry = Volatile.Read(ref _geometry);
        if (pointer && (geometry is null || input.Width != geometry.Width || input.Height != geometry.Height))
        { ReleaseAll(); return true; } // A normal resize can overtake the Mac's last presented frame.
        if (GetAncestor(GetForegroundWindow(), 2) != _hwnd)
        {
            if (!SetForegroundWindow(_hwnd) || GetAncestor(GetForegroundWindow(), 2) != _hwnd)
            { ReleaseAll(); FocusUnavailable = true; return false; }
        }
        List<NativeInput> events = [];
        if (pointer)
        {
            if (DwmGetWindowAttribute(_hwnd, 9, out var bounds, Marshal.SizeOf<Rect>()) < 0 || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
            if (bounds.Right - bounds.Left != geometry!.Width || bounds.Bottom - bounds.Top != geometry.Height)
            { ReleaseAll(); return true; } // Native resize arrived before its next WGC frame.
            var x = bounds.Left + input.X;
            var y = bounds.Top + input.Y;
            if (GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2) != _hwnd)
            { ReleaseAll(); FocusUnavailable = true; return false; }
            var vx = GetSystemMetrics(76); var vy = GetSystemMetrics(77);
            var vw = GetSystemMetrics(78); var vh = GetSystemMetrics(79);
            events.Add(Mouse(0x8000 | 0x4000 | 1, (int)((long)(x - vx) * 65535 / Math.Max(1, vw - 1)),
                (int)((long)(y - vy) * 65535 / Math.Max(1, vh - 1)), 0));
        }
        switch (input.Kind)
        {
            case "button": events.Add(Button(input.Code, input.IsDown)); break;
            case "wheel": events.Add(Mouse(0x0800, 0, 0, unchecked((uint)input.Code))); break;
            case "key": events.Add(Key(input.Code, input.IsDown)); break;
            case "text":
                foreach (var character in input.Text!)
                {
                    events.Add(new() { Type = 1, Union = new() { Keyboard = new() { Scan = character, Flags = 4, Extra = WindowsInputCapture.RoamingInjectedMarker } } });
                    events.Add(new() { Type = 1, Union = new() { Keyboard = new() { Scan = character, Flags = 6, Extra = WindowsInputCapture.RoamingInjectedMarker } } });
                }
                break;
        }
        Inject(events.ToArray());
        if (input.Kind == "key") { if (input.IsDown) _keys.Add(input.Code); else _keys.Remove(input.Code); }
        if (input.Kind == "button") { if (input.IsDown) _buttons.Add(input.Code); else _buttons.Remove(input.Code); }
        return true;
    }

    /// <summary>
    /// Win32 nonclient hit result at a physical pixel in the captured DWM frame. The network layer
    /// must first bind the request to its authenticated HWND/session. No focus or input changes.
    /// </summary>
    public static int HitTest(nint hwnd, int x, int y)
    {
        if (!OperatingSystem.IsWindows() || !IsWindow(hwnd) || IsIconic(hwnd) ||
            DwmGetWindowAttribute(hwnd, 9, out var bounds, Marshal.SizeOf<Rect>()) < 0 ||
            x < 0 || y < 0 || x >= bounds.Right - bounds.Left || y >= bounds.Bottom - bounds.Top) return 0;
        var screenX = bounds.Left + x; var screenY = bounds.Top + y;
        if (screenX is < short.MinValue or > short.MaxValue || screenY is < short.MinValue or > short.MaxValue) return 0;
        var packed = (nint)((screenY & 0xffff) << 16 | (screenX & 0xffff));
        return SendMessageTimeoutW(hwnd, 0x0084, 0, packed, 2, 100, out var hit) == 0 ? 0 : unchecked((int)hit);
    }

    private bool Resize(int width, int height)
    {
        WindowRoamingLimits.FrameBytes(width, height);
        ReleaseAll();
        // A maximized backing must become an ordinary source window before its physical size can
        // follow the proxy. This targets the bound HWND directly, without injecting a shortcut.
        if (IsZoomed(_hwnd)) ShowWindow(_hwnd, 9); // SW_RESTORE
        if (!GetWindowRect(_hwnd, out var outer) ||
            DwmGetWindowAttribute(_hwnd, 9, out var frame, Marshal.SizeOf<Rect>()) < 0 ||
            frame.Right <= frame.Left || frame.Bottom <= frame.Top) return false;
        var extraWidth = outer.Right - outer.Left - (frame.Right - frame.Left);
        var extraHeight = outer.Bottom - outer.Top - (frame.Bottom - frame.Top);
        var outerWidth = checked(width + extraWidth); var outerHeight = checked(height + extraHeight);
        if (outerWidth <= 0 || outerHeight <= 0) return false;
        // Invisible resize margins are absent from WGC frames, but included by SetWindowPos.
        // Native min/max constraints remain in force. The next real frame updates input geometry.
        return SetWindowPos(_hwnd, 0, outer.Left, outer.Top, outerWidth, outerHeight, 0x0014);
    }
    public void ReleaseAll()
    {
        var releases = _keys.Select(key => Key(key, false)).Concat(_buttons.Select(button => Button(button, false))).ToArray();
        _keys.Clear(); _buttons.Clear();
        if (releases.Length != 0)
        {
            // Teardown must complete even if UIPI/focus changed while the session was ending.
            try { Inject(releases); } catch (Win32Exception) { }
        }
    }
    private void Inject(NativeInput[] events)
    {
        if (events.Length == 0) return;
        void Send()
        {
            if (SendInput((uint)events.Length, events, Marshal.SizeOf<NativeInput>()) != events.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "窗口输入未能注入，请确认源程序为普通权限。");
        }
        if (_capture is not null) _capture.ExecuteRoamingInput(Send); else Send();
    }
    private static NativeInput Key(int key, bool down) => new() { Type = 1, Union = new() { Keyboard = new()
    { VirtualKey = (ushort)key, Flags = (down ? 0u : 2u) | (key is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0xA3 or 0xA5 ? 1u : 0u), Extra = WindowsInputCapture.RoamingInjectedMarker } } };
    private static NativeInput Button(int button, bool down) => button switch
    {
        0 => Mouse(down ? 2u : 4u), 1 => Mouse(down ? 8u : 16u), 2 => Mouse(down ? 32u : 64u),
        3 or 4 => Mouse(down ? 0x80u : 0x100u, data: (uint)(button - 2)), _ => throw new ArgumentOutOfRangeException(nameof(button))
    };
    private static NativeInput Mouse(uint flags, int x = 0, int y = 0, uint data = 0) => new() { Type = 0, Union = new() { Mouse = new() { X = x, Y = y, Data = data, Flags = flags, Extra = WindowsInputCapture.RoamingInjectedMarker } } };
    public void Dispose() { if (_disposed) return; ReleaseAll(); _disposed = true; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint w, nint l, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint kind);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int metric);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out Rect rectangle, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
