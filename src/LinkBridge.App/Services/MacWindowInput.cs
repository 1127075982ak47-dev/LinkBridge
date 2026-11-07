using LinkBridge.Core.WindowRoaming;
using LinkBridge.Input;

namespace LinkBridge.App.Services;

/// <summary>AX-verified window-bound input in the already-authorized main Mac process.</summary>
public sealed class MacWindowInput : IDisposable
{
    private readonly Guid _sessionId;
    private readonly MacInputInjector _keyboard;
    private readonly MacScrollWheel _scrollWheel = new();
    private sealed record Geometry(int Width, int Height);
    private Geometry? _geometry;
    private nint _handle;
    private bool _disposed;
    public bool FocusUnavailable { get; private set; }
    public MacWindowInput(Guid sessionId, nint windowId)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (sessionId == Guid.Empty || windowId <= 0) throw new ArgumentException("无效的 Mac 窗口输入目标。");
        _sessionId = sessionId; _keyboard = new MacInputInjector(); _handle = MacWindowNative.InputStart(checked((uint)windowId));
        if (_handle == 0) throw new InvalidOperationException(MacInputInjector.IsTrusted()
            ? "Mac 源窗口尚未稳定或已关闭，请重新拖动。" : "Mac 源窗口不可访问，请确认辅助功能权限。");
    }
    public void UpdateFrameGeometry(int width, int height)
    { WindowRoamingLimits.FrameBytes(width, height); Volatile.Write(ref _geometry, new(width, height)); }
    public static int HitTest(nint windowId, int x, int y) => OperatingSystem.IsMacOS() && windowId > 0 ? MacWindowNative.HitTest(checked((uint)windowId), x, y) : 1;
    public bool Apply(WindowRemoteInput input)
    {
        FocusUnavailable = false;
        if (_disposed || input.SessionId != _sessionId) return false;
        if (input.Kind == "resize") return Resize(input.Width, input.Height);
        WindowRoamingLimits.Validate(input);
        if (input.Kind == "release") { ReleaseAll(); return true; }
        var pointer = input.Kind is "move" or "button" or "wheel";
        var geometry = Volatile.Read(ref _geometry);
        if (pointer && (geometry is null || input.Width != geometry.Width || input.Height != geometry.Height))
        { ReleaseAll(); return true; }
        int result;
        // The native bridge takes logical pixels for wheel events. Both input
        // routes share the same conversion so moving a window does not change speed.
        var code = input.Kind == "wheel" ? _scrollWheel.Convert(input.Code) : input.Code;
        if (input.Kind == "wheel" && code == 0) return true;
        if (pointer) result = MacWindowNative.InputPointer(_handle, input.Kind == "move" ? 0 : input.Kind == "button" ? 1 : 2,
            input.X, input.Y, code, input.IsDown ? 1 : 0, input.Width, input.Height);
        else
        {
            if (input.Kind == "text") result = MacWindowNative.InputText(_handle, input.Text!);
            else
            {
                result = MacWindowNative.InputFocus(_handle);
                if (result == 0) _keyboard.Apply(new(InputEventType.Key, input.IsDown ? (byte)0 : (byte)1, (ushort)input.Code, 0, 0));
            }
        }
        if (result == 0) return true;
        ReleaseAll(); FocusUnavailable = result == -2;
        return result == 2; // A resize may legitimately overtake the presented frame.
    }
    public bool Resize(int width, int height)
    {
        WindowRoamingLimits.FrameBytes(width, height);
        return !_disposed && MacWindowNative.InputResize(_handle, width, height) == 0;
    }
    public void ReleaseAll()
    { _scrollWheel.Reset(); _keyboard.ReleaseAll(); if (_handle != 0) MacWindowNative.InputRelease(_handle); }
    public void Dispose()
    {
        if (_disposed) return; ReleaseAll(); _disposed = true;
        var handle = Interlocked.Exchange(ref _handle, 0); if (handle != 0) MacWindowNative.InputStop(handle);
    }
}
