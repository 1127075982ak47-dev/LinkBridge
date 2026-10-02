using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LinkBridge.Input;

/// <summary>Windows is left of the Mac. Pointer crossing selects the active machine.</summary>
public sealed class WindowsInputCapture : IDisposable
{
    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private const int WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
    private const int WmMouseMove = 0x200, WmLButtonDown = 0x201, WmLButtonUp = 0x202;
    private const int WmRButtonDown = 0x204, WmRButtonUp = 0x205;
    private const int WmMButtonDown = 0x207, WmMButtonUp = 0x208, WmMouseWheel = 0x20A;

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData
    {
        public Point Point;
        public uint MouseInfo, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyData
    {
        public uint VkCode, ScanCode, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int metric);
    [DllImport("user32.dll")] private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? module);

    private readonly Action<InputPacket> _send;
    private readonly Func<bool> _isConnected;
    private readonly HookProc _keyboardProc, _mouseProc;
    private readonly HashSet<ushort> _heldKeys = [];
    private readonly HashSet<byte> _heldButtons = [];
    private readonly HashSet<ushort> _physicalKeys = [];
    private readonly HashSet<byte> _physicalButtons = [];
    private readonly int _remoteWidth, _remoteHeight;
    private IntPtr _keyboard, _mouse;
    private Point _anchor, _lastLocal;
    private int _localWidth, _localHeight, _remoteX, _remoteY, _cursorHideCalls;
    private bool _remote, _exitHotkeyHeld, _controlDown, _altDown, _edgeBlockedByHeldInput, _fileDrag;
    private long _lastDisplayRefreshTick;

    public event Action? RemoteEntered;
    public event Action? RemoteExited;
    public event Action? ExitRequested;
    /// <summary>Raised on the input hook thread after the remote left-button release was sent.</summary>
    public event Action? FileDragReleased;
    /// <summary>Raised on the input hook thread when Escape, return to Windows, or disconnect cancels the drag.</summary>
    public event Action? FileDragCancelled;
    public bool IsActive => _keyboard != IntPtr.Zero && _mouse != IntPtr.Zero;
    public bool IsRemote => _remote;
    public int LocalWidth => _localWidth;
    public int LocalHeight => _localHeight;
    public int LastEntryX { get; private set; }

    /// <summary>
    /// On the capture's dispatcher thread, enter normal remote control for a deliberate Mac
    /// screenshot action. Invoke after the initiating UI button has been released.
    /// </summary>
    public bool TryEnterRemoteForScreenshot()
    {
        if (!IsActive || !_isConnected()) return false;
        if (_remote) return true;
        if (_physicalKeys.Count != 0 || _physicalButtons.Count != 0 || !GetCursorPos(out var position)) return false;
        RefreshDisplayBounds();
        EnterRemote(position.X, position.Y);
        return true;
    }

    /// <summary>
    /// Call on the same dispatcher thread as Start, only after the native file drop target has cancelled
    /// Explorer's local OLE operation. Ordinary held-button drags never enter the remote machine.
    /// The FileDrag packet arms Mac's synthetic held button without clicking the destination.
    /// </summary>
    public bool BeginFileDrag()
    {
        if (!IsActive || _remote || !_isConnected() || (GetAsyncKeyState(1) & 0x8000) == 0 ||
            _physicalKeys.Count != 0 || !GetCursorPos(out var position)) return false;
        RefreshDisplayBounds();
        _fileDrag = true;
        _heldButtons.Add(0);
        // Place the Mac pointer with ordinary movement first. Arming before these reset packets
        // would turn the -100000/-100000 reset into a dragged event over unrelated Mac content.
        EnterRemote(position.X, position.Y);
        _send(new InputPacket(InputEventType.FileDrag, 1, 0, 0, 0));
        return true;
    }

    public WindowsInputCapture(Action<InputPacket> send, Func<bool> isConnected, int remoteWidth, int remoteHeight)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (remoteWidth <= 0 || remoteHeight <= 0) throw new ArgumentOutOfRangeException(nameof(remoteWidth));
        _send = send;
        _isConnected = isConnected;
        _remoteWidth = remoteWidth;
        _remoteHeight = remoteHeight;
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;
    }

    public void Start()
    {
        if (IsActive) return;
        _localWidth = GetSystemMetrics(0);
        _localHeight = GetSystemMetrics(1);
        _lastDisplayRefreshTick = Environment.TickCount64;
        _physicalKeys.Clear();
        _physicalButtons.Clear();
        // A key might already be down when the hooks are installed. Do not lose its eventual local key-up.
        for (ushort key = 8; key < 256; key++)
            if (key is not (0x10 or 0x11 or 0x12) && (GetAsyncKeyState(key) & 0x8000) != 0) _physicalKeys.Add(key);
        foreach (var (vk, button) in new[] { (1, (byte)0), (2, (byte)1), (4, (byte)2), (5, (byte)3), (6, (byte)4) })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) _physicalButtons.Add(button);
        GetCursorPos(out _lastLocal);
        var module = GetModuleHandleW(null);
        _keyboard = SetWindowsHookExW(WhKeyboardLl, _keyboardProc, module, 0);
        _mouse = SetWindowsHookExW(WhMouseLl, _mouseProc, module, 0);
        if (!IsActive)
        {
            var error = Marshal.GetLastWin32Error();
            Stop();
            throw new Win32Exception(error, "无法安装跨屏键鼠钩子。");
        }
    }

    public void Stop()
    {
        LeaveRemote();
        var keyboard = _keyboard; var mouse = _mouse;
        _keyboard = IntPtr.Zero; _mouse = IntPtr.Zero;
        if (keyboard != IntPtr.Zero) UnhookWindowsHookEx(keyboard);
        if (mouse != IntPtr.Zero) UnhookWindowsHookEx(mouse);
        _physicalKeys.Clear(); _physicalButtons.Clear();
        _edgeBlockedByHeldInput = false;
    }

    public void LeaveRemote()
    {
        if (!_remote) return;
        var cancelledFileDrag = _fileDrag;
        _fileDrag = false;
        _remote = false;
        if (cancelledFileDrag)
        {
            // Returning to Windows while holding a file is cancellation, never a Finder drop.
            // Cancellation reaches the ordered remote queue before any held-button releases.
            // On disconnect these sends fail closed and the receiver's session cleanup releases.
            _send(new InputPacket(InputEventType.Key, 0, 0x1B, 0, 0));
            _send(new InputPacket(InputEventType.Key, 1, 0x1B, 0, 0));
            _send(new InputPacket(InputEventType.FileDrag, 0, 0, 0, 0));
        }
        foreach (var key in _heldKeys) _send(new InputPacket(InputEventType.Key, 1, key, 0, 0));
        _heldKeys.Clear();
        foreach (var button in _heldButtons) _send(new InputPacket(InputEventType.Button, button, 0, 0, 0));
        _heldButtons.Clear();
        for (var i = 0; i < _cursorHideCalls; i++) ShowCursor(true);
        _cursorHideCalls = 0;
        var localY = Math.Clamp((int)((long)_remoteY * _localHeight / _remoteHeight), 1, _localHeight - 2);
        _lastLocal = new Point { X = _localWidth - 32, Y = localY };
        SetCursorPos(_lastLocal.X, _lastLocal.Y);
        _controlDown = _altDown = _exitHotkeyHeld = false;
        if (cancelledFileDrag) FileDragCancelled?.Invoke();
        RemoteExited?.Invoke();
    }

    private void EnterRemote(int localX, int localY)
    {
        _remote = true;
        LastEntryX = localX;
        _remoteX = 64;
        _remoteY = Math.Clamp((int)((long)localY * _remoteHeight / _localHeight), 1, _remoteHeight - 2);
        // The existing protocol uses relative movement. Reset to Mac's left/top,
        // then place the pointer at the corresponding height on its left edge.
        _send(new InputPacket(InputEventType.Move, 0, 0, -100000, -100000));
        _send(new InputPacket(InputEventType.Move, 0, 0, _remoteX, _remoteY));
        _anchor = new Point { X = _localWidth / 2, Y = _localHeight / 2 };
        while (_cursorHideCalls < 8)
        {
            _cursorHideCalls++;
            if (ShowCursor(false) < 0) break;
        }
        SetCursorPos(_anchor.X, _anchor.Y);
        RemoteEntered?.Invoke();
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || !IsActive) return CallNextHookEx(_keyboard, code, message, data);
        var evt = Marshal.PtrToStructure<KeyData>(data);
        var kind = message.ToInt32();
        var down = kind is WmKeyDown or WmSysKeyDown;
        var up = kind is WmKeyUp or WmSysKeyUp;
        if (!down && !up) return CallNextHookEx(_keyboard, code, message, data);
        if ((evt.Flags & 0x10) == 0)
        {
            var physicalKey = (ushort)evt.VkCode;
            if (down) _physicalKeys.Add(physicalKey); else _physicalKeys.Remove(physicalKey);
        }
        if (_remote && !_isConnected()) LeaveRemote();
        if (!_remote) return CallNextHookEx(_keyboard, code, message, data);

        // The injected Escape used to cancel Explorer is deliberately ignored, just like all other
        // injected keyboard input. A physical Escape cancels the remote file transaction.
        if (_fileDrag && evt.VkCode == 0x1B && down && (evt.Flags & 0x10) == 0)
        {
            _fileDrag = false;
            // Finder treats mouse-up as accepting the promised file. Send Escape through the
            // ordered input channel first, so its native drag loop cancels before any release.
            // This branch owns the complete Escape pair; the physical key-up needs no matching
            // remote key release and ordinary remote Escape retains its existing behavior.
            _send(new InputPacket(InputEventType.Key, 0, 0x1B, 0, 0));
            _send(new InputPacket(InputEventType.Key, 1, 0x1B, 0, 0));
            if (_heldButtons.Remove(0)) _send(new InputPacket(InputEventType.Button, 0, 0, 0, 0));
            _send(new InputPacket(InputEventType.FileDrag, 0, 0, 0, 0));
            FileDragCancelled?.Invoke();
            return (IntPtr)1;
        }

        if (evt.VkCode is 0x11 or 0xA2 or 0xA3) _controlDown = down;
        if (evt.VkCode is 0x12 or 0xA4 or 0xA5) _altDown = down;
        if (evt.VkCode == 0x4D && _controlDown && _altDown)
        {
            if (down && !_exitHotkeyHeld)
            {
                _exitHotkeyHeld = true;
                ThreadPool.QueueUserWorkItem(_ => ExitRequested?.Invoke());
            }
            if (up) _exitHotkeyHeld = false;
            return (IntPtr)1;
        }
        if ((evt.Flags & 0x10) == 0)
        {
            var key = (ushort)evt.VkCode;
            if (down) _heldKeys.Add(key); else _heldKeys.Remove(key);
            _send(new InputPacket(InputEventType.Key, up ? (byte)1 : (byte)0, key, 0, 0));
        }
        return (IntPtr)1;
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || !IsActive) return CallNextHookEx(_mouse, code, message, data);
        var evt = Marshal.PtrToStructure<MouseData>(data);
        var kind = message.ToInt32();
        RefreshDisplayBounds();
        if ((evt.Flags & 1) == 0)
        {
            if (kind is WmLButtonDown or WmLButtonUp or WmRButtonDown or WmRButtonUp or WmMButtonDown or WmMButtonUp)
            {
                var button = kind is WmLButtonDown or WmLButtonUp ? (byte)0 : kind is WmRButtonDown or WmRButtonUp ? (byte)1 : (byte)2;
                if (kind is WmLButtonDown or WmRButtonDown or WmMButtonDown) _physicalButtons.Add(button);
                else _physicalButtons.Remove(button);
            }
            else if (kind is 0x20B or 0x20C) // WM_XBUTTONDOWN/UP
            {
                var button = (byte)(2 + (evt.MouseInfo >> 16));
                if (kind == 0x20B) _physicalButtons.Add(button); else _physicalButtons.Remove(button);
            }
        }
        if (_remote && !_isConnected()) LeaveRemote();
        if (!_remote)
        {
            if (kind == WmMouseMove)
            {
                var atEdge = evt.Point.X >= _localWidth - 2;
                var held = _physicalKeys.Count != 0 || _physicalButtons.Count != 0;
                var entering = (evt.Flags & 1) == 0 && _physicalKeys.Count == 0 && _physicalButtons.Count == 0 &&
                    atEdge && (evt.Point.X > _lastLocal.X || _edgeBlockedByHeldInput) && _isConnected();
                _edgeBlockedByHeldInput = atEdge && held;
                _lastLocal = evt.Point;
                if (entering) { EnterRemote(evt.Point.X, evt.Point.Y); return (IntPtr)1; }
            }
            return CallNextHookEx(_mouse, code, message, data);
        }

        if (kind == WmMouseMove)
        {
            var dx = evt.Point.X - _anchor.X;
            var dy = evt.Point.Y - _anchor.Y;
            if (dx == 0 && dy == 0) return (IntPtr)1;
            if (dx < 0 && _remoteX + dx <= 0) { LeaveRemote(); return (IntPtr)1; }
            _remoteX = Math.Clamp(_remoteX + dx, 0, _remoteWidth - 1);
            _remoteY = Math.Clamp(_remoteY + dy, 0, _remoteHeight - 1);
            _send(new InputPacket(InputEventType.Move, 0, 0, dx, dy));
            SetCursorPos(_anchor.X, _anchor.Y);
        }
        else if (kind is WmLButtonDown or WmLButtonUp or WmRButtonDown or WmRButtonUp or WmMButtonDown or WmMButtonUp)
        {
            var button = kind is WmLButtonDown or WmLButtonUp ? (byte)0
                : kind is WmRButtonDown or WmRButtonUp ? (byte)1 : (byte)2;
            var down = kind is WmLButtonDown or WmRButtonDown or WmMButtonDown;
            if (down) _heldButtons.Add(button); else _heldButtons.Remove(button);
            _send(new InputPacket(InputEventType.Button, button, 0, down ? 1 : 0, 0));
            if (_fileDrag && button == 0 && !down && (evt.Flags & 1) == 0)
            {
                _fileDrag = false;
                FileDragReleased?.Invoke();
            }
        }
        else if (kind == WmMouseWheel)
        {
            _send(new InputPacket(InputEventType.Wheel, 0, 0, unchecked((short)(evt.MouseInfo >> 16)), 0));
        }
        return (IntPtr)1;
    }

    private void RefreshDisplayBounds()
    {
        var now = Environment.TickCount64;
        if (now - _lastDisplayRefreshTick < 3000) return;
        _lastDisplayRefreshTick = now;
        var width = GetSystemMetrics(0); var height = GetSystemMetrics(1);
        if (width <= 1 || height <= 1 || (width == _localWidth && height == _localHeight)) return;
        _localWidth = width; _localHeight = height;
        if (_remote) LeaveRemote();
        else GetCursorPos(out _lastLocal);
    }

    public void Dispose() => Stop();
}
