using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using LinkBridge.Core.WindowRoaming;

namespace LinkBridge.App.Services;

public sealed record WindowCaptureTarget(nint Hwnd, int ProcessId, string Title)
{
    public override string ToString() => Title;
}

/// <summary>The single subscriber owns this pooled frame and must dispose after use or supersession.</summary>
public sealed class WindowCaptureFrame : IDisposable
{
    private byte[]? _data;
    internal WindowCaptureFrame(byte[] data, int width, int height, int sequence, long timestamp)
    { _data = data; Width = width; Height = height; Sequence = sequence; Timestamp100ns = timestamp; }
    public byte[] Data => _data ?? throw new ObjectDisposedException(nameof(WindowCaptureFrame));
    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public int Length => checked(Stride * Height);
    public int Sequence { get; }
    public long Timestamp100ns { get; }
    public void Dispose() { var data = Interlocked.Exchange(ref _data, null); if (data is not null) ArrayPool<byte>.Shared.Return(data); }
}

/// <summary>Per-HWND native WGC capture; Frame executes off the UI thread and must only queue work.</summary>
public sealed class WindowsWindowCapture : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void NativeFrame(nint context, nint pixels, int width, int height, int stride, int sequence, long timestamp);
    private readonly NativeFrame _callback;
    private nint _capture;
    private int _running, _disposed;
    private Timer? _health;
    public event Action<WindowCaptureFrame>? Frame;
    public event Action<string>? Failed;
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public WindowsWindowCapture() => _callback = ReceiveFrame;

    public static IReadOnlyList<WindowCaptureTarget> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) return [];
        List<WindowCaptureTarget> targets = [];
        EnumWindowProc enumeration = (window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window) || GetWindow(window, 4) != nint.Zero) return true;
            if (window == GetDesktopWindow() || window == GetShellWindow()) return true;
            var extendedStyle = GetWindowLongPtrW(window, -20).ToInt64();
            if ((extendedStyle & (0x80L | 0x08000000L)) != 0) return true; // TOOLWINDOW / NOACTIVATE utility surfaces.
            if (DwmGetCloaked(window, 14, out var cloaked, sizeof(uint)) >= 0 && cloaked != 0) return true;
            var className = new StringBuilder(256);
            GetClassNameW(window, className, className.Capacity);
            // These are Windows shell window classes, independent of display language or product titles.
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                or "TrayNotifyWnd" or "TrayRocketView") return true;
            GetWindowThreadProcessId(window, out var pid);
            if (pid == Environment.ProcessId) return true;
            if (IsWindowsShellSurface(pid)) return true;
            var title = new StringBuilder(513);
            GetWindowTextW(window, title, title.Capacity);
            if (title.Length != 0) targets.Add(new(window, checked((int)pid), title.ToString()));
            return true;
        };
        EnumWindows(enumeration, nint.Zero); GC.KeepAlive(enumeration);
        return targets;
    }

    private static bool IsWindowsShellSurface(uint processId)
    {
        var process = OpenProcess(0x1000, false, processId); // QUERY_LIMITED_INFORMATION only.
        if (process == nint.Zero) return false;
        try
        {
            var path = new StringBuilder(32768); var length = path.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, path, ref length)) return false;
            var fullPath = path.ToString();
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);
            if (windows.Length == 0 || !fullPath.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            // Match only operating-system binaries in the Windows tree, not app names or captions.
            return Path.GetFileName(fullPath).ToLowerInvariant() is "textinputhost.exe" or "inputapp.exe"
                or "shellexperiencehost.exe" or "startmenuexperiencehost.exe" or "searchhost.exe"
                or "searchui.exe" or "lockapp.exe" or "sihost.exe";
        }
        finally { CloseHandle(process); }
    }

    public void Start(nint hwnd, int maximumFps = 30)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (hwnd == nint.Zero || !IsWindow(hwnd) || IsIconic(hwnd) || maximumFps is < 1 or > 120)
            throw new ArgumentException("请选择有效、非最小化的 Windows 窗口。");
        Stop();
        Volatile.Write(ref _running, 1);
        try
        {
            var result = LBWC_Start(hwnd, _callback, nint.Zero, maximumFps, out _capture);
            Marshal.ThrowExceptionForHR(result);
            _health = new Timer(_ => PollHealth(), null, 500, 500);
        }
        catch { Volatile.Write(ref _running, 0); throw; }
    }

    private void ReceiveFrame(nint context, nint pixels, int width, int height, int stride, int sequence, long timestamp)
    {
        if (!IsRunning) return;
        WindowCaptureFrame? owned = null;
        try
        {
            var length = WindowRoamingLimits.FrameBytes(width, height);
            if (pixels == nint.Zero || stride != width * 4 || sequence <= 0) return;
            var data = ArrayPool<byte>.Shared.Rent(length);
            try { Marshal.Copy(pixels, data, 0, length); }
            catch { ArrayPool<byte>.Shared.Return(data); throw; }
            owned = new(data, width, height, sequence, timestamp);
            var subscriber = Frame;
            if (subscriber is null) { owned.Dispose(); return; }
            subscriber(owned); owned = null;
        }
        catch { owned?.Dispose(); EmitFailure("窗口画面采集未完成。"); }
    }

    private void PollHealth()
    {
        // Stop disposes this timer before native destruction. A health poll never manipulates UI.
        var handle = _capture;
        if (handle == nint.Zero || !IsRunning) return;
        try
        {
            if (LBWC_GetStatus(handle, out var status) < 0 || status == 0) return;
            if (Interlocked.Exchange(ref _running, 0) != 0)
                EmitFailure(status == 1 ? "源窗口已关闭。" : "Windows 窗口捕获已停止。");
        }
        catch { }
    }

    private void EmitFailure(string message) => ThreadPool.QueueUserWorkItem(_ =>
    {
        // Subscriber teardown must not run on a native frame callback or this timer callback,
        // because Stop waits for those callbacks before freeing the native capture handle.
        try { Failed?.Invoke(message); } catch { }
    });

    public void Stop()
    {
        Volatile.Write(ref _running, 0);
        var health = Interlocked.Exchange(ref _health, null);
        if (health is not null) health.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var handle = Interlocked.Exchange(ref _capture, nint.Zero);
        if (handle != nint.Zero) LBWC_Stop(handle);
        GC.KeepAlive(_callback);
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Stop(); }
    private delegate bool EnumWindowProc(nint hwnd, nint context);
    [DllImport("LinkBridge.WindowCapture.dll", CallingConvention = CallingConvention.StdCall)] private static extern int LBWC_Start(nint hwnd, NativeFrame callback, nint context, int fps, out nint handle);
    [DllImport("LinkBridge.WindowCapture.dll", CallingConvention = CallingConvention.StdCall)] private static extern int LBWC_Stop(nint handle);
    [DllImport("LinkBridge.WindowCapture.dll", CallingConvention = CallingConvention.StdCall)] private static extern int LBWC_GetStatus(nint handle, out int status);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint context);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int capacity);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetCloaked(nint hwnd, uint attribute, out uint cloaked, int size);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder path, ref int capacity);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int capacity);
}
