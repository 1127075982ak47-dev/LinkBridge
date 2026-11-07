using System.Buffers;
using System.Runtime.InteropServices;
using LinkBridge.Core.WindowRoaming;

namespace LinkBridge.App.Services;

/// <summary>In-process ScreenCaptureKit capture of one complete native Mac window.</summary>
public sealed class MacWindowCapture : IDisposable
{
    private readonly MacWindowNative.FrameCallback _frameCallback;
    private readonly MacWindowNative.ErrorCallback _errorCallback;
    private nint _handle;
    private int _running, _disposed;
    public event Action<WindowCaptureFrame>? Frame;
    public event Action<string>? Failed;
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public static bool HasScreenRecordingPermission => OperatingSystem.IsMacOS() && MacWindowNative.ScreenPermission() != 0;
    public MacWindowCapture() { _frameCallback = ReceiveFrame; _errorCallback = ReceiveError; }
    public void Start(nint windowId, int maximumFps = 30)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (windowId <= 0 || maximumFps is < 1 or > 30) throw new ArgumentException("无效的 Mac 窗口或帧率。");
        if (!HasScreenRecordingPermission) throw new InvalidOperationException("Mac 联桥尚未获得屏幕录制权限，请在隐私与安全性中允许屏幕录制后重开联桥。");
        Stop(); Volatile.Write(ref _running, 1);
        _handle = MacWindowNative.CaptureStart(checked((uint)windowId), maximumFps, _frameCallback, _errorCallback, 0);
        if (_handle == 0) { Volatile.Write(ref _running, 0); throw new InvalidOperationException("Mac 原生窗口捕获未能启动。"); }
    }
    private void ReceiveFrame(nint context, nint pixels, int width, int height, int stride, int sequence, long timestamp)
    {
        if (!IsRunning) return;
        WindowCaptureFrame? frame = null;
        try
        {
            var length = WindowRoamingLimits.FrameBytes(width, height);
            if (pixels == 0 || stride < width * 4 || sequence <= 0) return;
            var data = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                if (stride == width * 4) Marshal.Copy(pixels, data, 0, length);
                else for (var row = 0; row < height; row++) Marshal.Copy(pixels + row * stride, data, row * width * 4, width * 4);
            }
            catch { ArrayPool<byte>.Shared.Return(data); throw; }
            frame = new(data, width, height, sequence, timestamp);
            var subscriber = Frame;
            if (subscriber is null) { frame.Dispose(); return; }
            subscriber(frame); frame = null;
        }
        catch { frame?.Dispose(); Fail("Mac 窗口画面采集未完成。"); }
    }
    private void ReceiveError(nint context, nint utf8) => Fail(Marshal.PtrToStringUTF8(utf8) ?? "Mac 窗口捕获已停止。");
    private void Fail(string message)
    {
        Volatile.Write(ref _running, 0);
        ThreadPool.QueueUserWorkItem(_ => { try { Failed?.Invoke(message); } catch { } });
    }
    public void Stop()
    {
        Volatile.Write(ref _running, 0);
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) MacWindowNative.CaptureStop(handle);
        GC.KeepAlive(_frameCallback); GC.KeepAlive(_errorCallback);
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Stop(); }
}

internal static class MacWindowNative
{
    private const string Library = "libLinkBridge.WindowRoaming.dylib";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FrameCallback(nint context, nint pixels, int width, int height, int stride, int sequence, long timestamp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ErrorCallback(nint context, nint utf8);
    [DllImport(Library, EntryPoint = "LBMW_ScreenPermission")] internal static extern int ScreenPermission();
    [DllImport(Library, EntryPoint = "LBMW_CaptureStart")] internal static extern nint CaptureStart(uint id, int fps, FrameCallback frame, ErrorCallback error, nint context);
    [DllImport(Library, EntryPoint = "LBMW_CaptureStop")] internal static extern void CaptureStop(nint handle);
    [DllImport(Library, EntryPoint = "LBMW_EdgeStart")] internal static extern nint EdgeStart();
    [DllImport(Library, EntryPoint = "LBMW_EdgePoll")] internal static extern nint EdgePoll(nint handle, int enabled);
    [DllImport(Library, EntryPoint = "LBMW_EdgeMark")] internal static extern void EdgeMark(nint handle, uint id, int borrowed);
    [DllImport(Library, EntryPoint = "LBMW_EdgeRestore")] internal static extern int EdgeRestore(nint handle, uint id);
    [DllImport(Library, EntryPoint = "LBMW_EdgeResume")] internal static extern int EdgeResume(nint handle, uint id, int offsetX, int offsetY);
    [DllImport(Library, EntryPoint = "LBMW_EdgeStop")] internal static extern void EdgeStop(nint handle);
    [DllImport(Library, EntryPoint = "LBMW_FreeString")] internal static extern void FreeString(nint text);
    [DllImport(Library, EntryPoint = "LBMW_InputStart")] internal static extern nint InputStart(uint id);
    [DllImport(Library, EntryPoint = "LBMW_InputFocus")] internal static extern int InputFocus(nint handle);
    [DllImport(Library, EntryPoint = "LBMW_InputPointer")] internal static extern int InputPointer(nint handle, int kind, int x, int y, int code, int down, int width, int height);
    [DllImport(Library, EntryPoint = "LBMW_InputText")] internal static extern int InputText(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Library, EntryPoint = "LBMW_InputResize")] internal static extern int InputResize(nint handle, int width, int height);
    [DllImport(Library, EntryPoint = "LBMW_InputRelease")] internal static extern void InputRelease(nint handle);
    [DllImport(Library, EntryPoint = "LBMW_InputStop")] internal static extern void InputStop(nint handle);
    [DllImport(Library, EntryPoint = "LBMW_HitTest")] internal static extern int HitTest(uint id, int x, int y);
}
