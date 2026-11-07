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
    [DllImport(Quartz)] private static extern void CGEventSetFlags(IntPtr evt, ulong flags);
    [DllImport(Quartz)] private static extern ulong CGEventSourceFlagsState(int state);
    [DllImport(Quartz)] private static extern IntPtr CGWindowListCopyWindowInfo(uint options, uint relativeToWindow);
    [DllImport(Quartz)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dictionary, out Rect rect);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, int type, out long value);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);

    private readonly EditingShortcutState _keys;
    private readonly HashSet<byte> _heldButtons = [];
    private readonly MacScrollWheel _scrollWheel = new();
    private bool _fileDragArmed;
    private Rect _bounds;
    private Point? _pendingDisplayOrigin;
    private double _x;
    private double _y;

    public MacInputInjector()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        _keys = new EditingShortcutState(PostNativeKey, FrontmostApplication.BundleIdentifier,
            (CGEventSourceFlagsState(0) & (1UL << 16)) != 0);
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
            case InputEventType.DisplayOrigin:
                _pendingDisplayOrigin = Math.Abs((long)packet.X) <= 65536 && Math.Abs((long)packet.Y) <= 65536 ? new Point(packet.X,packet.Y) : null;
                break;
            case InputEventType.DisplaySize:
                if (_pendingDisplayOrigin is { } origin && NativeScreens.Read().Any(s => s.X == origin.X && s.Y == origin.Y && s.Width == packet.X && s.Height == packet.Y))
                    _bounds = new Rect(origin,new Size(packet.X,packet.Y));
                _pendingDisplayOrigin = null;
                break;
            case InputEventType.Key:
                _keys.Apply(packet.Key, packet.Code == 0);
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
                var pixels = _scrollWheel.Convert(packet.X);
                if (pixels == 0) break;
                var evt = CGEventCreateScrollWheelEvent(IntPtr.Zero, 0, 1, pixels);
                if (evt != IntPtr.Zero) CGEventSetFlags(evt, _keys.Flags);
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
        _scrollWheel.Reset();
        _keys.ReleaseAll();
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
        _keys.InjectNativeKey(key, down);
    }

    private static void PostNativeKey(ushort key, bool down, ulong flags)
    {
        var evt = CGEventCreateKeyboardEvent(IntPtr.Zero, key, down);
        if (evt != IntPtr.Zero) CGEventSetFlags(evt, flags);
        Post(evt);
    }

    private void PostMouse(int type, int button)
    {
        var evt = CGEventCreateMouseEvent(IntPtr.Zero, type, new Point(_x, _y), button);
        if (evt != IntPtr.Zero) CGEventSetFlags(evt, _keys.Flags);
        Post(evt);
    }

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

    // Separating physical state from posted state lets one editing chord use
    // Command without turning unrelated Control shortcuts into Command shortcuts.
    internal sealed class EditingShortcutState(Action<ushort, bool, ulong> post, Func<string?> frontmostBundle, bool capsLock = false)
    {
        private readonly HashSet<ushort> _physical = [];
        private readonly HashSet<ushort> _posted = [];
        private readonly Dictionary<ushort, (ushort Key, bool Redo)> _editing = [];
        private bool _capsLock = capsLock;

        internal ulong Flags => (_capsLock ? 1UL << 16 : 0) |
            (_posted.Any(k => k is 56 or 60) ? 1UL << 17 : 0) |
            (_posted.Any(k => k is 59 or 62) ? 1UL << 18 : 0) |
            (_posted.Any(k => k is 58 or 61) ? 1UL << 19 : 0) |
            (_posted.Any(k => k is 54 or 55) ? 1UL << 20 : 0);

        internal void Apply(ushort vk, bool down)
        {
            if (!TryMacKey(vk, out var raw)) return;
            if (down)
            {
                var newlyPressed = _physical.Add(raw);
                if (raw == 57 && newlyPressed) _capsLock = !_capsLock;
                if (!_editing.ContainsKey(vk) && newlyPressed &&
                    _physical.Any(k => k is 59 or 62) &&
                    !_physical.Any(k => k is 54 or 55 or 58 or 61) &&
                    TryEditingChord(vk, frontmostBundle(), out var mapped, out var redo))
                    _editing.Add(vk, (mapped, redo));
                ReconcileModifiers();
                if (!IsModifier(raw))
                    InjectNativeKey(_editing.TryGetValue(vk, out var chord) ? chord.Key : raw, true);
            }
            else
            {
                var target = _editing.TryGetValue(vk, out var chord) ? chord.Key : raw;
                // Keep the chord's Command/Shift through its letter-up. Only
                // afterwards restore a Control key that is still physically held.
                _physical.Remove(raw);
                if (!IsModifier(raw) && _posted.Contains(target) &&
                    !_editing.Any(pair => pair.Key != vk && pair.Value.Key == target) &&
                    !_physical.Any(key => key == target && !_editing.Any(pair => TryMacKey(pair.Key, out var original) && original == key)))
                    InjectNativeKey(target, false);
                _editing.Remove(vk);
                ReconcileModifiers();
            }
        }

        private void ReconcileModifiers()
        {
            var desired = _physical.Where(IsModifier).ToHashSet();
            if (_editing.Count != 0)
            {
                desired.Remove(59); desired.Remove(62); desired.Add(55);
                if (_editing.Values.Any(chord => chord.Redo) && !desired.Any(k => k is 56 or 60)) desired.Add(56);
            }
            foreach (var key in _posted.Where(IsModifier).Where(key => !desired.Contains(key)).ToArray()) InjectNativeKey(key, false);
            foreach (var key in desired.Where(key => !_posted.Contains(key))) InjectNativeKey(key, true);
        }

        internal void InjectNativeKey(ushort key, bool down)
        {
            if (down) _posted.Add(key); else _posted.Remove(key);
            post(key, down, Flags);
        }

        internal void ReleaseAll()
        {
            _physical.Clear(); _editing.Clear();
            foreach (var key in _posted.OrderBy(IsModifier).ToArray()) InjectNativeKey(key, false);
        }

        private static bool IsModifier(ushort key) => key is 54 or 55 or 56 or 58 or 59 or 60 or 61 or 62;

        internal static bool TryEditingChord(ushort vk, string? bundle, out ushort key, out bool redo)
        {
            redo = vk == 0x59;
            key = redo ? (ushort)6 : (ushort)0; // Windows Ctrl+Y becomes Mac Command+Shift+Z.
            if (vk is not (0x43 or 0x56 or 0x58 or 0x41 or 0x5A or 0x59 or 0x53 or 0x46)) return false;
            // Paste is consistently Command+V, including terminal emulators.
            // Unknown front apps otherwise keep Control rather than risking a
            // terminal interrupt, suspend or shell line-editing command.
            if (vk != 0x56 && (string.IsNullOrWhiteSpace(bundle) || IsTerminal(bundle))) return false;
            return redo || TryMacKey(vk, out key);
        }

        private static bool IsTerminal(string bundle) => bundle.Equals("com.apple.Terminal", StringComparison.OrdinalIgnoreCase) ||
            bundle.Equals("com.googlecode.iterm2", StringComparison.OrdinalIgnoreCase) ||
            bundle.StartsWith("dev.warp.", StringComparison.OrdinalIgnoreCase) ||
            bundle.Equals("net.kovidgoyal.kitty", StringComparison.OrdinalIgnoreCase) ||
            bundle.StartsWith("org.alacritty", StringComparison.OrdinalIgnoreCase) ||
            bundle.StartsWith("com.mitchellh.ghostty", StringComparison.OrdinalIgnoreCase) ||
            bundle.StartsWith("org.wezfurlong.wezterm", StringComparison.OrdinalIgnoreCase) ||
            bundle.Equals("co.zeit.hyper", StringComparison.OrdinalIgnoreCase) ||
            bundle.StartsWith("org.tabby", StringComparison.OrdinalIgnoreCase);
    }

    private static class FrontmostApplication
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private static readonly nint AppKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern nint Class([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);

        internal static string? BundleIdentifier()
        {
            nint pool = 0;
            try
            {
                _ = AppKit;
                pool = Send(Send(Class("NSAutoreleasePool"), Selector("alloc")), Selector("init"));
                var workspace = Send(Class("NSWorkspace"), Selector("sharedWorkspace"));
                var app = Send(workspace, Selector("frontmostApplication"));
                var identifier = Send(app, Selector("bundleIdentifier"));
                return identifier == 0 ? null : Marshal.PtrToStringUTF8(Send(identifier, Selector("UTF8String")));
            }
            catch { return null; }
            finally { if (pool != 0) Send(pool, Selector("drain")); }
        }
    }
}
