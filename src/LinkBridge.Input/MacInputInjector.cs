using System.Runtime.InteropServices;

namespace LinkBridge.Input;

/// <summary>Injects physical key positions and pointer events into the logged-in macOS session.</summary>
public sealed class MacInputInjector
{
    private const string Quartz = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [StructLayout(LayoutKind.Sequential)] private struct Point(double x, double y)
    {
        public double X = x;
        public double Y = y;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Size(double width, double height)
    {
        public double Width = width;
        public double Height = height;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect(Point origin, Size size)
    {
        public Point Origin = origin;
        public Size Size = size;
    }

    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrusted();
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);
    [DllImport(ApplicationServices)] private static extern IntPtr AXUIElementCreateSystemWide();
    [DllImport(ApplicationServices)] private static extern int AXUIElementCopyElementAtPosition(IntPtr systemWide, float x, float y, out IntPtr element);
    [DllImport(ApplicationServices)] private static extern int AXUIElementGetPid(IntPtr element, out int pid);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreate(
        IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(Quartz)] private static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(Quartz)] private static extern Point CGEventGetLocation(IntPtr evt);
    [DllImport(Quartz)] private static extern uint CGMainDisplayID();
    [DllImport(Quartz)] private static extern Rect CGDisplayBounds(uint display);
    [DllImport(Quartz)] private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort key, [MarshalAs(UnmanagedType.I1)] bool down);
    [DllImport(Quartz)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, int type, Point point, int button);
    [DllImport(Quartz, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr CGEventCreateScrollWheelEvent(IntPtr source, int units, uint wheels, int delta);
    [DllImport(Quartz)] private static extern void CGEventPost(int tap, IntPtr evt);
    [DllImport(Quartz)] private static extern IntPtr CGWindowListCopyWindowInfo(uint options, uint relativeToWindow);
    [DllImport(Quartz)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dictionary, out Rect rect);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, int type, out long value);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);

    private readonly HashSet<ushort> _heldKeys = [];
    private readonly HashSet<byte> _heldButtons = [];
    private bool _fileDragArmed;
    private readonly Rect _bounds;
    private double _x;
    private double _y;

    public MacInputInjector()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        _bounds = CGDisplayBounds(CGMainDisplayID());
        var evt = CGEventCreate(IntPtr.Zero);
        try
        {
            var point = CGEventGetLocation(evt);
            _x = point.X;
            _y = point.Y;
        }
        finally { CFRelease(evt); }
    }

    public static bool IsTrusted() => OperatingSystem.IsMacOS() && AXIsProcessTrusted();

    /// <summary>Presses only after the exact bundle-local helper window is the frontmost hit at that point.</summary>
    public static bool BeginFileDragOnOwnedWindow(int helperPid, int windowNumber, double x, double y, out string diagnostic)
    {
        diagnostic = FormattableString.Invariant($"source expected pid={helperPid} window={windowNumber} point=({x:0.###},{y:0.###})");
        if (!IsTrusted() || helperPid <= 0 || windowNumber <= 0 || !double.IsFinite(x) || !double.IsFinite(y))
        { diagnostic += "; invalid request or accessibility unavailable"; return false; }
        var windows = CGWindowListCopyWindowInfo(1 | 16, 0);
        if (windows == IntPtr.Zero) { diagnostic += "; CGWindowList unavailable"; return false; }
        var numberKey = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowNumber", 0x08000100);
        var ownerKey = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowOwnerPID", 0x08000100);
        var boundsKey = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowBounds", 0x08000100);
        try
        {
            long firstPid = -1, firstId = -1;
            var ownWindowExistsAtPoint = false;
            for (nint i = 0; i < CFArrayGetCount(windows); i++)
            {
                var dictionary = CFArrayGetValueAtIndex(windows, i);
                var bounds = CFDictionaryGetValue(dictionary, boundsKey);
                if (bounds == IntPtr.Zero || !CGRectMakeWithDictionaryRepresentation(bounds, out var rect) ||
                    x < rect.Origin.X || x >= rect.Origin.X + rect.Size.Width ||
                    y < rect.Origin.Y || y >= rect.Origin.Y + rect.Size.Height) continue;
                var owner = CFDictionaryGetValue(dictionary, ownerKey);
                var number = CFDictionaryGetValue(dictionary, numberKey);
                if (owner == IntPtr.Zero || number == IntPtr.Zero || !CFNumberGetValue(owner, 4, out var pid) ||
                    !CFNumberGetValue(number, 4, out var id))
                { diagnostic += "; first candidate metadata unavailable"; return false; }
                if (firstId < 0)
                {
                    firstPid = pid; firstId = id;
                    diagnostic += FormattableString.Invariant($"; first candidate pid={pid} window={id} bounds=({rect.Origin.X:0.###},{rect.Origin.Y:0.###},{rect.Size.Width:0.###},{rect.Size.Height:0.###})");
                }
                if (pid == helperPid && id == windowNumber)
                {
                    ownWindowExistsAtPoint = true;
                    diagnostic += FormattableString.Invariant($"; exact source bounds=({rect.Origin.X:0.###},{rect.Origin.Y:0.###},{rect.Size.Width:0.###},{rect.Size.Height:0.###})");
                    break;
                }
            }
            if (!ownWindowExistsAtPoint) { diagnostic += "; exact source window absent at point"; return false; }
            // NotificationCenter/WindowSharing can expose a fullscreen mouse-
            // ignoring window above normal apps. Geometry alone is not hit-test.
            // Require both the exact native source window and actual AX hit PID.
            var systemWide = AXUIElementCreateSystemWide();
            if (systemWide == IntPtr.Zero) { diagnostic += "; AX hit-test unavailable"; return false; }
            IntPtr hit = IntPtr.Zero;
            try
            {
                var hitError = AXUIElementCopyElementAtPosition(systemWide, (float)x, (float)y, out hit);
                var hitPid = -1;
                var pidError = hit != IntPtr.Zero ? AXUIElementGetPid(hit, out hitPid) : -1;
                diagnostic += $"; AX hit error={hitError} pidError={pidError} pid={hitPid}";
                if (hitError != 0 || pidError != 0 || hitPid != helperPid) return false;
            }
            finally { if (hit != IntPtr.Zero) CFRelease(hit); CFRelease(systemWide); }
            Post(CGEventCreateMouseEvent(IntPtr.Zero, 1, new Point(x, y), 0));
            return true;
        }
        finally { CFRelease(numberKey); CFRelease(ownerKey); CFRelease(boundsKey); CFRelease(windows); }
    }

    /// <summary>Releases the press created on our own drag window if startup/copy is cancelled.</summary>
    public static void ReleaseFileDragPress()
    {
        if (!IsTrusted()) return;
        var evt = CGEventCreate(IntPtr.Zero);
        if (evt == IntPtr.Zero) return;
        try { Post(CGEventCreateMouseEvent(IntPtr.Zero, 2, CGEventGetLocation(evt), 0)); }
        finally { CFRelease(evt); }
    }

    public static bool RequestTrust()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        var library = NativeLibrary.Load(CoreFoundation);
        var appServices = NativeLibrary.Load(ApplicationServices);
        var key = Marshal.ReadIntPtr(NativeLibrary.GetExport(appServices, "kAXTrustedCheckOptionPrompt"));
        var value = Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCFBooleanTrue"));
        var keyCallbacks = NativeLibrary.GetExport(library, "kCFTypeDictionaryKeyCallBacks");
        var valueCallbacks = NativeLibrary.GetExport(library, "kCFTypeDictionaryValueCallBacks");
        var options = CFDictionaryCreate(IntPtr.Zero, [key], [value], 1, keyCallbacks, valueCallbacks);
        try { return AXIsProcessTrustedWithOptions(options); }
        finally
        {
            CFRelease(options);
            NativeLibrary.Free(appServices);
            NativeLibrary.Free(library);
        }
    }

    public void Apply(InputPacket packet)
    {
        switch (packet.Type)
        {
            case InputEventType.Key:
                if (TryMacKey(packet.Key, out var key)) PostKey(key, packet.Code == 0);
                break;
            case InputEventType.Move:
                _x = Math.Clamp(_x + packet.X, _bounds.Origin.X, _bounds.Origin.X + _bounds.Size.Width - 1);
                _y = Math.Clamp(_y + packet.Y, _bounds.Origin.Y, _bounds.Origin.Y + _bounds.Size.Height - 1);
                PostMouse(_heldButtons.Contains(0) ? 6 : _heldButtons.Contains(1) ? 7 : 5, 0);
                break;
            case InputEventType.Button:
                if (packet.Code > 2) return;
                if (packet.X != 0) _heldButtons.Add(packet.Code);
                else
                {
                    _heldButtons.Remove(packet.Code);
                    if (packet.Code == 0) _fileDragArmed = false;
                }
                PostMouse(packet.Code switch
                {
                    0 => packet.X != 0 ? 1 : 2,
                    1 => packet.X != 0 ? 3 : 4,
                    _ => packet.X != 0 ? 25 : 26,
                }, packet.Code);
                break;
            case InputEventType.Wheel:
                var evt = CGEventCreateScrollWheelEvent(IntPtr.Zero, 0, 1, packet.X / 120);
                Post(evt);
                break;
            case InputEventType.FileDrag:
                // AppKit owns the native file-promise source window. Arming the
                // logical button must not click whatever app is under the pointer.
                if (packet.Code == 1) { _heldButtons.Add(0); _fileDragArmed = true; }
                else
                {
                    CancelArmedFileDrag();
                    if (_heldButtons.Remove(0)) PostMouse(2, 0);
                }
                break;
        }
    }

    public void ReleaseAll()
    {
        foreach (var key in _heldKeys.ToArray()) PostKey(key, false);
        // A broken input connection must abort the native drag before release.
        // Normal Button-up cleared the flag, so a successful drop gets no Escape.
        CancelArmedFileDrag();
        foreach (var button in _heldButtons.ToArray())
            Apply(new InputPacket(InputEventType.Button, button, 0, 0, 0));
    }

    private void CancelArmedFileDrag()
    {
        if (!_fileDragArmed) return;
        _fileDragArmed = false;
        if (!_heldButtons.Contains(0)) return;
        PostKey(53, true); PostKey(53, false);
    }

    private void PostKey(ushort key, bool down)
    {
        if (down) _heldKeys.Add(key); else _heldKeys.Remove(key);
        Post(CGEventCreateKeyboardEvent(IntPtr.Zero, key, down));
    }

    private void PostMouse(int type, int button) => Post(CGEventCreateMouseEvent(IntPtr.Zero, type, new Point(_x, _y), button));

    private static void Post(IntPtr evt)
    {
        if (evt == IntPtr.Zero) return;
        try { CGEventPost(0, evt); }
        finally { CFRelease(evt); }
    }

    private static bool TryMacKey(ushort vk, out ushort key)
    {
        // Windows virtual-key -> macOS hardware key position (US layout).
        var mapped = vk switch
        {
            0x41 => 0, 0x53 => 1, 0x44 => 2, 0x46 => 3, 0x48 => 4, 0x47 => 5,
            0x5A => 6, 0x58 => 7, 0x43 => 8, 0x56 => 9, 0x42 => 11,
            0x51 => 12, 0x57 => 13, 0x45 => 14, 0x52 => 15, 0x59 => 16, 0x54 => 17,
            0x31 => 18, 0x32 => 19, 0x33 => 20, 0x34 => 21, 0x36 => 22, 0x35 => 23,
            0xBB => 24, 0x39 => 25, 0x37 => 26, 0xBD => 27, 0x38 => 28, 0x30 => 29,
            0xDD => 30, 0x4F => 31, 0x55 => 32, 0xDB => 33, 0x49 => 34, 0x50 => 35,
            0x0D => 36, 0x4C => 37, 0x4A => 38, 0xDE => 39, 0x4B => 40,
            0xBA => 41, 0xDC => 42, 0xBC => 43, 0xBF => 44, 0x4E => 45, 0x4D => 46,
            0xBE => 47, 0x09 => 48, 0x20 => 49, 0xC0 => 50, 0x08 => 51, 0x1B => 53,
            0x5B => 55, 0x5C => 54, 0x10 or 0xA0 => 56, 0xA1 => 60,
            0x11 or 0xA2 => 59, 0xA3 => 62, 0x12 or 0xA4 => 58, 0xA5 => 61,
            0x14 => 57, 0x70 => 122, 0x71 => 120, 0x72 => 99, 0x73 => 118,
            0x74 => 96, 0x75 => 97, 0x76 => 98, 0x77 => 100, 0x78 => 101,
            0x79 => 109, 0x7A => 103, 0x7B => 111,
            0x25 => 123, 0x27 => 124, 0x28 => 125, 0x26 => 126,
            0x24 => 115, 0x23 => 119, 0x21 => 116, 0x22 => 121, 0x2E => 117,
            _ => -1,
        };
        key = mapped >= 0 ? (ushort)mapped : (ushort)0;
        return mapped >= 0;
    }
}
