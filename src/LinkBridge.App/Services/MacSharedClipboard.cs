using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace LinkBridge.App.Services;

/// <summary>Native Mac text/PNG clipboard. It never exports paths, HTML or startup history.</summary>
public sealed class MacSharedClipboard : IDisposable
{
    internal const int TextLimit = 1024 * 1024;
    internal const int PngLimit = 32 * 1024 * 1024;
    private DispatcherTimer? _timer;
    private long _lastChange;
    private int _started, _disposed, _capture;
    public event Action<SharedClipboardContent>? Changed;
    public event Action<string>? Diagnostic;

    public void Start()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        void StartOnUi()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _lastChange = Native.ChangeCount(); // Existing clipboard is not broadcast.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += Poll;
            _timer.Start();
        }
        if (Dispatcher.UIThread.CheckAccess()) StartOnUi(); else Dispatcher.UIThread.Post(StartOnUi);
    }

    private void Poll(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var count = Native.ChangeCount();
            if (count == _lastChange) return;
            _lastChange = count;
            var content = ReadNative();
            if (content is null) return;
            // An explicit copy of the same value is meaningful after a file copy
            // or pause. Only the native sequence from our own Apply suppresses echo.
            Changed?.Invoke(content);
        }
        catch { try { Diagnostic?.Invoke("Mac 剪贴板暂时无法读取"); } catch { } }
    }

    public async Task ApplyAsync(SharedClipboardContent content, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        void ApplyOnUi()
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ApplyNative(content);
            _lastChange = Native.ChangeCount();
        }
        if (Dispatcher.UIThread.CheckAccess()) ApplyOnUi(); else await Dispatcher.UIThread.InvokeAsync(ApplyOnUi);
    }

    public void BeginScreenshot()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _capture, 1, 0) != 0) return;
        try
        {
            var process = new Process { StartInfo = new ProcessStartInfo("/usr/sbin/screencapture")
                { UseShellExecute = false, CreateNoWindow = true }, EnableRaisingEvents = true };
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add("-c");
            process.Exited += (_, _) => { Interlocked.Exchange(ref _capture, 0); process.Dispose(); };
            if (!process.Start()) throw new IOException("系统截图未能启动");
        }
        catch
        {
            Interlocked.Exchange(ref _capture, 0);
            try { Diagnostic?.Invoke("Mac 系统截图未能启动"); } catch { }
            throw;
        }
    }

    internal static SharedClipboardContent? ReadNative(nint privateBoard = 0)
    {
        using var pool = new Native.Pool();
        var board = privateBoard != 0 ? privateBoard : Native.Board();
        // Finder copies can also advertise text aliases or a TIFF file icon.
        // File clipboard intent must be recognized before image/text fallbacks.
        if (Native.HasType(board, "public.file-url") || Native.HasType(board, "NSFilenamesPboardType")) return null;
        var png = Native.ReadData(board, "public.png", PngLimit);
        if (png is not null) return new("png", png);
        var tiff = Native.DataForType(board, "public.tiff");
        if (tiff != 0 && (nuint)Native.Send(tiff, Native.Selector("length")) <= 128u * 1024 * 1024)
        {
            var bitmap = Native.Send1(Native.Send(Native.Class("NSBitmapImageRep"), Native.Selector("alloc")),
                Native.Selector("initWithData:"), tiff);
            if (bitmap != 0)
            {
                try
                {
                    var properties = Native.Send(Native.Class("NSDictionary"), Native.Selector("dictionary"));
                    var encoded = Native.Send2(bitmap, Native.Selector("representationUsingType:properties:"), 4, properties);
                    var data = Native.CopyData(encoded, PngLimit);
                    if (data is not null) return new("png", data);
                }
                finally { Native.Send(bitmap, Native.Selector("release")); }
            }
        }
        using var type = new Native.Text("public.utf8-plain-text");
        var text = Native.Send1(board, Native.Selector("stringForType:"), type.Value);
        if (text == 0) return null;
        var utf8 = Native.Send1(text, Native.Selector("dataUsingEncoding:"), 4);
        var bytes = Native.CopyData(utf8, TextLimit);
        return bytes is null ? null : new("text", bytes);
    }

    internal static void ApplyNative(SharedClipboardContent content, nint privateBoard = 0)
    {
        if (content.Kind is not ("text" or "png") || content.Data.Length > (content.Kind == "text" ? TextLimit : PngLimit))
            throw new ArgumentException("Unsupported or oversized clipboard content.", nameof(content));
        using var pool = new Native.Pool();
        using var type = new Native.Text(content.Kind == "png" ? "public.png" : "public.utf8-plain-text");
        var board = privateBoard != 0 ? privateBoard : Native.Board();
        var pin = GCHandle.Alloc(content.Data, GCHandleType.Pinned);
        try
        {
            var data = Native.SendData(Native.Class("NSData"), Native.Selector("dataWithBytes:length:"), pin.AddrOfPinnedObject(), (nuint)content.Data.Length);
            if (data == 0) throw new IOException("无法建立 Mac 剪贴板数据");
            Native.Send(board, Native.Selector("clearContents"));
            if (!Native.SendBool2(board, Native.Selector("setData:forType:"), data, type.Value))
                throw new IOException("Mac 原生剪贴板拒绝写入");
        }
        finally { pin.Free(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        void Stop() { _timer?.Stop(); if (_timer is not null) _timer.Tick -= Poll; _timer = null; }
        if (Dispatcher.UIThread.CheckAccess()) Stop(); else Dispatcher.UIThread.Post(Stop);
    }

    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private static readonly nint AppKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        private static readonly ConcurrentDictionary<string, nint> Selectors = new();
        [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern nint GetSelector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint Send(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint Send1(nint receiver, nint selector, nint value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint Send2(nint receiver, nint selector, nint value, nint second);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] internal static extern nint SendData(nint receiver, nint selector, nint bytes, nuint length);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SendBool2(nint receiver, nint selector, nint data, nint type);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SendBool1(nint receiver, nint selector, nint value);
        [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
        [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
        internal static nint Class(string name) { _ = AppKit; return GetClass(name); }
        internal static nint Selector(string name) => Selectors.GetOrAdd(name, GetSelector);
        internal static nint Board() => Send(Class("NSPasteboard"), Selector("generalPasteboard"));
        internal static long ChangeCount() { using var pool = new Pool(); return (long)Send(Board(), Selector("changeCount")); }
        internal static nint DataForType(nint board, string name)
        { using var type = new Text(name); return Send1(board, Selector("dataForType:"), type.Value); }
        internal static bool HasType(nint board, string name)
        { using var type = new Text(name); return SendBool1(Send(board, Selector("types")), Selector("containsObject:"), type.Value); }
        internal static byte[]? ReadData(nint board, string name, int limit) => CopyData(DataForType(board, name), limit);
        internal static byte[]? CopyData(nint data, int limit)
        {
            if (data == 0) return null;
            var length = (long)Send(data, Selector("length"));
            if (length < 0 || length > limit) return null;
            var bytes = new byte[(int)length];
            if (length > 0) Marshal.Copy(Send(data, Selector("bytes")), bytes, 0, bytes.Length);
            return bytes;
        }
        internal sealed class Text(string value) : IDisposable
        {
            internal nint Value { get; } = CFStringCreateWithCString(0, value, 0x08000100);
            public void Dispose() { if (Value != 0) CFRelease(Value); }
        }
        internal sealed class Pool : IDisposable
        {
            private readonly nint _pool = Send(Send(Class("NSAutoreleasePool"), Selector("alloc")), Selector("init"));
            public void Dispose() { if (_pool != 0) Send(_pool, Selector("drain")); }
        }
    }
}
