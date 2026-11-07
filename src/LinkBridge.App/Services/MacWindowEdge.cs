using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;

namespace LinkBridge.App.Services;

/// <summary>Real native caption drags; mouse-only tap records edge crossing before KVM release.</summary>
public sealed class MacWindowEdge : IDisposable
{
    private readonly Func<bool> _enabled;
    private readonly DispatcherTimer _timer;
    private nint _handle;
    private bool _disposed;
    public event Action<nint, string, int, int, double>? HandoffRequested;
    public event Action<string>? Diagnostic;
    public MacWindowEdge(Func<bool> enabled)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        _enabled = enabled; _handle = MacWindowNative.EdgeStart();
        if (_handle == 0) throw new InvalidOperationException("Mac 原生窗口边缘监听未能启动，请确认辅助功能权限。");
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += Poll; _timer.Start();
    }
    private void Poll(object? sender, EventArgs args)
    {
        if (_disposed || _handle == 0) return;
        nint text = 0;
        try
        {
            text = MacWindowNative.EdgePoll(_handle, _enabled() ? 1 : 0);
            if (text == 0) return;
            using var json = JsonDocument.Parse(Marshal.PtrToStringUTF8(text)!);
            var value = json.RootElement;
            if (value.TryGetProperty("diagnostic", out var diagnostic)) { Diagnostic?.Invoke(diagnostic.GetString() ?? ""); return; }
            HandoffRequested?.Invoke((nint)value.GetProperty("id").GetUInt32(), value.GetProperty("title").GetString() ?? "Mac 窗口",
                value.GetProperty("offsetX").GetInt32(), value.GetProperty("offsetY").GetInt32(), value.GetProperty("scale").GetDouble());
        }
        catch (Exception error) { Diagnostic?.Invoke("Mac 窗口接力失败：" + error.GetType().Name); }
        finally { if (text != 0) MacWindowNative.FreeString(text); }
    }
    public void MarkBorrowed(nint windowId, bool value)
    { if (!_disposed && _handle != 0 && windowId > 0) MacWindowNative.EdgeMark(_handle, checked((uint)windowId), value ? 1 : 0); }
    public void Restore(nint windowId)
    { if (!_disposed && _handle != 0 && windowId > 0) MacWindowNative.EdgeRestore(_handle, checked((uint)windowId)); }
    /// <summary>Restores the saved native window and resumes caption drag at the current Mac pointer. Call before Restore.</summary>
    public bool ResumeDrag(nint windowId, int offsetX, int offsetY) => !_disposed && _handle != 0 && windowId > 0 &&
        MacWindowNative.EdgeResume(_handle, checked((uint)windowId), offsetX, offsetY) == 0;
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _timer.Stop(); _timer.Tick -= Poll;
        var handle = Interlocked.Exchange(ref _handle, 0); if (handle != 0) MacWindowNative.EdgeStop(handle);
    }
}
