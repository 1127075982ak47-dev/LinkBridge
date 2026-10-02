using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace LinkBridge.App.Services;

/// <summary>
/// Native OLE receiver at the primary display's right edge. Only an actual CF_HDROP drag can
/// hand off. Source OLE is cancelled (DROPEFFECT_NONE) before the callback; source files stay put.
/// The callback runs on the private STA thread and must dispatch to the input capture's owner.
/// It must not perform file IO synchronously. SetEnabled follows authenticated input availability.
/// </summary>
public sealed class WindowsFileDragEdge : IDisposable
{
    private const uint WmClose = 0x0010, WmDestroy = 0x0002, WmTimer = 0x0113, WmMouseActivate = 0x0021;
    private const uint WsPopup = 0x80000000, ExToolWindow = 0x80, ExLayered = 0x80000, ExNoActivate = 0x8000000;
    private const uint SwpNoActivate = 0x10, SwpShowWindow = 0x40, SwpNoOwnerZOrder = 0x200;
    private const int StripPixels = 10;
    private readonly Action<IReadOnlyList<string>> _handoff;
    private readonly Action<string>? _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WindowProc _windowProc;
    private readonly DropTarget _dropTarget;
    private volatile bool _enabled, _disposed;
    private IntPtr _window;
    private Exception? _startError;
    private bool _visible, _dropRegistered, _awaitingLeave;
    private IReadOnlyList<string>? _pendingPaths;
    private uint _sourceThread;
    private long _cancelStarted, _leaveObserved;
    private string? _className;
    private int _screenWidth, _screenHeight;
    private bool _edgeHitLogged;

    public WindowsFileDragEdge(Action<IReadOnlyList<string>> handoff, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
        _log = log;
        _windowProc = ProcessMessage;
        _dropTarget = new DropTarget(this);
        _thread = new Thread(Run) { IsBackground = true, Name = "LinkBridge file OLE edge" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("文件拖放接收器启动超时。");
        if (_startError != null) throw new InvalidOperationException("无法启动文件拖放接收器。", _startError);
    }

    public void SetEnabled(bool enabled) => _enabled = enabled && !_disposed;

    private void Run()
    {
        // Avalonia sets DPI context on its UI thread. This dedicated native STA must set its own
        // context or GetSystemMetrics/window positioning can be virtualized (e.g. 2560 vs 3840
        // at 150% scaling), leaving the OLE strip away from the physical screen edge.
        var previousDpi = SetThreadDpiAwarenessContext((IntPtr)(-4)); // PER_MONITOR_AWARE_V2
        if (previousDpi == IntPtr.Zero)
        {
            _startError = new Win32Exception(Marshal.GetLastWin32Error(), "无法建立文件边缘的物理像素坐标。");
            _ready.Set(); return;
        }
        var oleResult = OleInitialize(IntPtr.Zero);
        if (oleResult < 0)
        {
            _startError = Marshal.GetExceptionForHR(oleResult); _ready.Set();
            SetThreadDpiAwarenessContext(previousDpi); return;
        }
        try
        {
            _className = "LinkBridge.FileDragEdge." + Guid.NewGuid().ToString("N");
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandleW(null),
                Procedure = _windowProc, ClassName = _className,
                Background = GetStockObject(4) // BLACK_BRUSH, 1/255 alpha below.
            };
            if (RegisterClassExW(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _screenWidth = GetSystemMetrics(0); _screenHeight = GetSystemMetrics(1);
            _window = CreateWindowExW(ExToolWindow | ExLayered | ExNoActivate, _className,
                "LinkBridge File Edge", WsPopup, _screenWidth - StripPixels, 0, StripPixels,
                _screenHeight, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Alpha zero is excluded from native OLE hit testing. Alpha one is visually imperceptible.
            if (!SetLayeredWindowAttributes(_window, 0, 1, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.ThrowExceptionForHR(RegisterDragDrop(_window, _dropTarget));
            _dropRegistered = true;
            SetTimer(_window, (UIntPtr)1, 20, IntPtr.Zero);
            _log?.Invoke($"原生文件边缘已注册 · PID {Environment.ProcessId} · {_screenWidth}×{_screenHeight} · 边缘 {StripPixels}px");
            _ready.Set();
            while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message); DispatchMessageW(ref message);
            }
        }
        catch (Exception exception) { _startError = exception; _log?.Invoke(exception.Message); _ready.Set(); }
        finally
        {
            if (_dropRegistered) { RevokeDragDrop(_window); _dropRegistered = false; }
            if (_window != IntPtr.Zero) { DestroyWindow(_window); _window = IntPtr.Zero; }
            if (_className != null) UnregisterClassW(_className, GetModuleHandleW(null));
            OleUninitialize();
            SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    private IntPtr ProcessMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == WmTimer) { Poll(); return IntPtr.Zero; }
        if (message == WmMouseActivate) return (IntPtr)3; // MA_NOACTIVATE
        if (message == WmClose)
        {
            _enabled = false; ResetPending(); ShowWindow(hwnd, 0);
            PostQuitMessage(0); return IntPtr.Zero;
        }
        if (message == WmDestroy) return IntPtr.Zero;
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void Poll()
    {
        var leftDown = (GetAsyncKeyState(1) & 0x8000) != 0;
        if (!leftDown) _edgeHitLogged = false;
        // Keep the target present until OLE itself calls DragLeave. Hiding it immediately after
        // DragEnter would manufacture a DragLeave before the injected Escape was processed.
        var desiredVisible = _enabled && leftDown && (_pendingPaths == null || _awaitingLeave);
        var width = GetSystemMetrics(0); var height = GetSystemMetrics(1);
        if (desiredVisible && (!_visible || width != _screenWidth || height != _screenHeight))
        {
            _screenWidth = width; _screenHeight = height;
            SetWindowPos(_window, (IntPtr)(-1), width - StripPixels, 0, StripPixels, height,
                SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder);
            _visible = true;
        }
        else if (!desiredVisible && _visible) { ShowWindow(_window, 0); _visible = false; }
        if (_visible && !_edgeHitLogged && GetCursorPos(out var pointer) && pointer.X >= _screenWidth - StripPixels)
        {
            _edgeHitLogged = true;
            _log?.Invoke($"文件边缘命中检测 · ({pointer.X},{pointer.Y}) · 原生窗口命中={WindowFromPoint(pointer) == _window}");
        }
        if (_pendingPaths == null) return;
        var now = Environment.TickCount64;
        if (!_enabled || !leftDown || now - _cancelStarted > 1500)
        {
            _log?.Invoke(!_enabled ? "文件接管已取消：连接或本机控制已切换。" : !leftDown
                ? "文件接管已取消：本机 OLE 结束前左键已松开，请持续按住滑入 Mac。"
                : "本机拖放未及时结束，请松开鼠标后重试。");
            ResetPending(); return;
        }
        // DragLeave proves OLE noticed Escape. Wait for the source's capture to be released as well;
        // otherwise pointer recentering could race Explorer's still-running modal DoDragDrop loop.
        if (_awaitingLeave || now - _leaveObserved < 40) return;
        var gui = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (_sourceThread == 0 || !GetGUIThreadInfo(_sourceThread, ref gui))
        {
            ResetPending(); _log?.Invoke("无法确认本机拖放已结束；未接管鼠标。"); return;
        }
        if (gui.Capture != IntPtr.Zero) return;
        var paths = _pendingPaths;
        ResetPending();
        _log?.Invoke($"本机 OLE 已结束并释放捕获 · 接管 {paths.Count} 项文件");
        try { _handoff(paths); }
        catch (Exception exception) { _log?.Invoke("文件跨屏接管失败：" + exception.Message); }
    }

    private void CancelSourceWithFiles(IDataObject data, uint keyState)
    {
        if (!_enabled || _pendingPaths != null || (keyState & 1) == 0) return;
        var format = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        var query = data.QueryGetData(ref format);
        if (query != 0) { _log?.Invoke("OLE 边缘拖动不是本机文件，未接管。"); return; }
        data.GetData(ref format, out var medium);
        List<string> paths = [];
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero) return;
            var count = DragQueryFileW(medium.unionmember, uint.MaxValue, null, 0);
            if (count is 0 or > 4096) return;
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFileW(medium.unionmember, index, null, 0);
                if (length is 0 or > 32767) return;
                var buffer = new StringBuilder(checked((int)length + 1));
                if (DragQueryFileW(medium.unionmember, index, buffer, length + 1) != length) return;
                var path = buffer.ToString();
                if (!Path.IsPathFullyQualified(path)) return;
                paths.Add(path);
            }
        }
        finally { ReleaseStgMedium(ref medium); }
        if (paths.Count == 0 || (GetAsyncKeyState(1) & 0x8000) == 0) return;
        _log?.Invoke($"原生 CF_HDROP 已识别 · {paths.Count} 项 · 正在取消本机 OLE");
        _pendingPaths = paths.AsReadOnly();
        _awaitingLeave = true;
        _cancelStarted = Environment.TickCount64;
        _sourceThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        Input[] escape =
        [
            new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = 0x1B } } },
            new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = 0x1B, Flags = 2 } } }
        ];
        if (SendInput((uint)escape.Length, escape, Marshal.SizeOf<Input>()) != escape.Length)
        {
            ResetPending();
            _log?.Invoke("无法取消本机拖放；文件仍保留在原位置。");
        }
    }

    private void ResetPending()
    {
        _pendingPaths = null; _awaitingLeave = false; _sourceThread = 0; _leaveObserved = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _enabled = false;
        var window = _window;
        if (window != IntPtr.Zero) PostMessageW(window, WmClose, UIntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        GC.KeepAlive(_dropTarget); GC.KeepAlive(_windowProc);
    }

    [ComVisible(true), Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleDropTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keyState, PointL point, ref uint effect);
        [PreserveSig] int DragOver(uint keyState, PointL point, ref uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keyState, PointL point, ref uint effect);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DropTarget(WindowsFileDragEdge owner) : IOleDropTarget
    {
        public int DragEnter(IDataObject data, uint keyState, PointL point, ref uint effect)
        {
            effect = 0;
            try { owner.CancelSourceWithFiles(data, keyState); }
            catch (Exception exception) { owner.ResetPending(); owner._log?.Invoke("文件拖放数据无效：" + exception.Message); }
            return 0;
        }
        public int DragOver(uint keyState, PointL point, ref uint effect) { effect = 0; return 0; }
        public int DragLeave()
        {
            if (owner._pendingPaths != null)
            {
                owner._awaitingLeave = false; owner._leaveObserved = Environment.TickCount64;
                owner._log?.Invoke("本机 OLE DragLeave 已确认 · 等待鼠标捕获释放");
            }
            return 0;
        }
        public int Drop(IDataObject data, uint keyState, PointL point, ref uint effect) { effect = 0; owner.ResetPending(); return 0; }
    }

    [StructLayout(LayoutKind.Sequential)] public struct PointL { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public IntPtr Hwnd;
        public uint Kind;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public PointL Point;
        public uint Private;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IOleDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(IntPtr drop, uint index, StringBuilder? path, uint capacity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? module);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int item);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint extended, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr order, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int metric);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointL point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PointL point);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint delay, IntPtr callback);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
}
