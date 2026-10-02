using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace LinkBridge.App.Services;

/// <summary>
/// Native Windows text/image clipboard adapter. Startup history and file-drop paths are never
/// emitted. Events run on a private STA thread; subscribers should queue their work immediately.
/// PNG is lossless and never resized. Remote images also expose standard DIB formats for paste.
/// </summary>
public sealed class WindowsSharedClipboard : IDisposable
{
    private const uint WmClipboardUpdate = 0x031D, WmTimer = 0x0113, WmApply = 0x8001, WmClose = 0x0010;
    private const uint CfUnicodeText = 13, CfDib = 8, CfDibV5 = 17, CfHDrop = 15;
    private const int TextLimit = 1024 * 1024, PngLimit = 32 * 1024 * 1024, DibLimit = 256 * 1024 * 1024;
    private const long PixelLimit = 64 * 1024 * 1024;
    private readonly ConcurrentQueue<ApplyRequest> _apply = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WindowProc _windowProc;
    private readonly UTF8Encoding _utf8 = new(false, true);
    private Thread? _thread;
    private IntPtr _window;
    private Exception? _startError;
    private uint _pngFormat, _seenSequence;
    private int _started, _disposed;
    private string? _className;
    private bool _listenerRegistered;
    private ApplyRequest? _currentApply;

    public event Action<SharedClipboardContent>? Changed;
    public event Action<string>? Diagnostic;

    public WindowsSharedClipboard() => _windowProc = ProcessMessage;

    public void Start()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "LinkBridge native clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("系统剪贴板监听启动超时。");
        if (_startError is not null) throw new InvalidOperationException("无法监听 Windows 剪贴板。", _startError);
    }

    public Task ApplyAsync(SharedClipboardContent content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _started) == 0 || _window == IntPtr.Zero) throw new InvalidOperationException("剪贴板服务未启动。");
        ValidateContent(content);
        ct.ThrowIfCancellationRequested();
        var request = new ApplyRequest(new SharedClipboardContent(content.Kind, content.Data.ToArray()), ct);
        _apply.Enqueue(request);
        PostMessageW(_window, WmApply, UIntPtr.Zero, IntPtr.Zero);
        return request.Completion.Task;
    }

    private void Run()
    {
        try
        {
            _className = "LinkBridge.SharedClipboard." + Guid.NewGuid().ToString("N");
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandleW(null),
                Procedure = _windowProc, ClassName = _className
            };
            if (RegisterClassExW(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _window = CreateWindowExW(0, _className, "LinkBridge Clipboard", 0, 0, 0, 0, 0,
                (IntPtr)(-3), IntPtr.Zero, windowClass.Instance, IntPtr.Zero); // HWND_MESSAGE, never a visible overlay.
            if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _pngFormat = RegisterClipboardFormatW("PNG");
            if (_pngFormat == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _seenSequence = GetClipboardSequenceNumber(); // Deliberately skip pre-existing clipboard history.
            if (!AddClipboardFormatListener(_window)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _listenerRegistered = true;
            SetTimer(_window, (UIntPtr)1, 500, IntPtr.Zero);
            _ready.Set();
            while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message); DispatchMessageW(ref message);
            }
        }
        catch (Exception error) { _startError = error; _ready.Set(); EmitDiagnostic("系统剪贴板监听已停止。"); }
        finally
        {
            if (_listenerRegistered) RemoveClipboardFormatListener(_window);
            if (_window != IntPtr.Zero) { DestroyWindow(_window); _window = IntPtr.Zero; }
            if (_className != null) UnregisterClassW(_className, GetModuleHandleW(null));
            _currentApply?.Completion.TrySetCanceled();
            while (_apply.TryDequeue(out var request)) request.Completion.TrySetCanceled();
        }
    }

    private IntPtr ProcessMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message is WmClipboardUpdate or WmTimer or WmApply)
        {
            try { ProcessApply(); ObserveClipboard(); }
            catch (Exception)
            { EmitDiagnostic("系统剪贴板内容暂时无法读取。"); }
            return IntPtr.Zero;
        }
        if (message == WmClose) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void ObserveClipboard()
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == _seenSequence || _currentApply is not null || Volatile.Read(ref _disposed) != 0) return;
        // Windows can synthesize CF_BITMAP from our concrete DIB and advance the sequence during
        // another app's paste. It still belongs to this private owner window, so it is our write,
        // not a fresh user copy. Ordinary Ctrl+C uses the source app's different owner window.
        if (GetClipboardOwner() == _window) { _seenSequence = sequence; return; }
        if (!OpenClipboard(_window)) return; // A busy clipboard is retried by the lightweight timer.
        SharedClipboardContent? content = null;
        try
        {
            // Explorer's copied files may advertise text paths as an alternate format. Never send them.
            if (!IsClipboardFormatAvailable(CfHDrop))
            {
                if (IsClipboardFormatAvailable(_pngFormat))
                {
                    var png = TrimPng(ReadGlobal(GetClipboardData(_pngFormat), PngLimit));
                    ValidatePng(png);
                    content = new("png", png);
                }
                else if (IsClipboardFormatAvailable(CfDibV5) || IsClipboardFormatAvailable(CfDib))
                {
                    var format = IsClipboardFormatAvailable(CfDibV5) ? CfDibV5 : CfDib;
                    var dib = ReadGlobal(GetClipboardData(format), DibLimit);
                    content = new("png", DibToPng(dib));
                }
                else if (IsClipboardFormatAvailable(CfUnicodeText))
                {
                    var textBytes = ReadGlobal(GetClipboardData(CfUnicodeText), TextLimit * 2 + 2);
                    var end = 0;
                    while (end + 1 < textBytes.Length && (textBytes[end] != 0 || textBytes[end + 1] != 0)) end += 2;
                    var text = Encoding.Unicode.GetString(textBytes, 0, end);
                    var utf8 = _utf8.GetBytes(text);
                    if (utf8.Length <= TextLimit) content = new("text", utf8);
                }
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or OverflowException)
        { EmitDiagnostic("此次剪贴板内容超出支持范围，未发送。"); }
        finally
        {
            // Rendering a delayed clipboard format can increment its sequence during GetClipboardData.
            _seenSequence = GetClipboardSequenceNumber(); CloseClipboard();
        }
        if (content is null) return;
        // A fresh user copy is intentional even when its bytes match an earlier item. Content
        // deduplication would incorrectly swallow A -> unsupported files -> A, or a copy after
        // sharing was paused. Only our own exact write sequence is suppressed above.
        try { Changed?.Invoke(content); }
        catch { EmitDiagnostic("剪贴板同步未完成。"); }
    }

    private void ProcessApply()
    {
        if (_currentApply is null && !_apply.TryDequeue(out _currentApply)) return;
        var request = _currentApply!;
        if (request.Token.IsCancellationRequested)
        {
            request.Completion.TrySetCanceled(request.Token); _currentApply = null; return;
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            request.Completion.TrySetCanceled(); _currentApply = null; return;
        }
        if (Environment.TickCount64 - request.Enqueued > 5000)
        {
            request.Completion.TrySetException(new IOException("Windows 剪贴板正被其他应用占用，请重试。"));
            _currentApply = null; return;
        }
        try
        {
            request.Prepared ??= PrepareFormats(request.Content);
            request.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!OpenClipboard(_window)) return;
            try
            {
                request.Token.ThrowIfCancellationRequested();
                if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
                foreach (var (format, data) in request.Prepared) WriteGlobal(format, data);
                // Record while still holding the lock: a user can copy something new immediately
                // after CloseClipboard, and that new sequence must not be mistaken for our write.
                _seenSequence = GetClipboardSequenceNumber();
            }
            finally { CloseClipboard(); }
            request.Completion.TrySetResult();
        }
        catch (OperationCanceledException) { request.Completion.TrySetCanceled(request.Token); }
        catch (Exception error) { request.Completion.TrySetException(error); }
        finally
        {
            if (request.Completion.Task.IsCompleted) _currentApply = null;
        }
    }

    private List<(uint Format, byte[] Data)> PrepareFormats(SharedClipboardContent content)
    {
        if (content.Kind == "text")
        {
            var text = _utf8.GetString(content.Data);
            return [(CfUnicodeText, Encoding.Unicode.GetBytes(text + '\0'))];
        }
        using var stream = new MemoryStream(content.Data, writable: false);
        using var bitmap = DecodeUnpremultiplied(stream);
        var width = bitmap.Width; var height = bitmap.Height;
        var rowBytes = checked(width * 4); var byteCount = checked(rowBytes * height);
        var bgra = new byte[byteCount];
        for (var row = 0; row < height; row++)
            Marshal.Copy(IntPtr.Add(bitmap.GetPixels(), checked(row * bitmap.RowBytes)), bgra, row * rowBytes, rowBytes);
        var dib = new byte[checked(40 + byteCount)];
        WriteBitmapHeader(dib, 40, width, height, byteCount);
        bgra.CopyTo(dib, 40);
        var dibV5 = new byte[checked(124 + byteCount)];
        WriteBitmapHeader(dibV5, 124, width, height, byteCount);
        BinaryPrimitives.WriteUInt32LittleEndian(dibV5.AsSpan(16), 3); // BI_BITFIELDS, preserving alpha.
        BinaryPrimitives.WriteUInt32LittleEndian(dibV5.AsSpan(40), 0x00ff0000);
        BinaryPrimitives.WriteUInt32LittleEndian(dibV5.AsSpan(44), 0x0000ff00);
        BinaryPrimitives.WriteUInt32LittleEndian(dibV5.AsSpan(48), 0x000000ff);
        BinaryPrimitives.WriteUInt32LittleEndian(dibV5.AsSpan(52), 0xff000000);
        BinaryPrimitives.WriteUInt32LittleEndian(dibV5.AsSpan(56), 0x73524742); // LCS_sRGB.
        BinaryPrimitives.WriteUInt32LittleEndian(dibV5.AsSpan(108), 4); // LCS_GM_IMAGES.
        bgra.CopyTo(dibV5, 124);
        return [(_pngFormat, content.Data), (CfDibV5, dibV5), (CfDib, dib)];
    }

    private static void WriteBitmapHeader(byte[] destination, uint size, int width, int height, int pixels)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, size);
        BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(8), -height); // Native top-down rows.
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(20), pixels);
        BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(24), 3780);
        BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(28), 3780);
    }

    private static byte[] DibToPng(byte[] dib)
    {
        if (dib.Length < 40) throw new IOException("Invalid clipboard DIB.");
        var header = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (header is not (40 or 52 or 56 or 108 or 124) || header > dib.Length) throw new IOException("Unsupported clipboard DIB header.");
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8));
        if (signedHeight == int.MinValue) throw new IOException("Invalid clipboard DIB height.");
        var height = Math.Abs(signedHeight); CheckDimensions(width, height);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        if (bits is not (1 or 4 or 8 or 16 or 24 or 32) || compression is not (0 or 3 or 6))
            throw new IOException("Unsupported clipboard DIB format.");
        var palette = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
        if (palette == 0 && bits <= 8) palette = 1u << bits;
        if (palette > 256) throw new IOException("Invalid clipboard DIB palette.");
        var masks = header == 40 ? compression == 3 ? 12 : compression == 6 ? 16 : 0 : 0;
        var offset = checked((int)header + (int)palette * 4 + masks);
        if (header == 124)
        {
            var profileAt = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(112));
            var profileBytes = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(116));
            if (profileAt == offset && profileBytes != 0) offset = checked(offset + (int)profileBytes);
        }
        var stride = checked(((width * bits + 31) / 32) * 4);
        if ((long)offset + (long)stride * height > dib.Length) throw new IOException("Truncated clipboard DIB.");
        var bmp = new byte[checked(14 + dib.Length)];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), checked(14 + offset));
        dib.CopyTo(bmp, 14);
        using var input = new MemoryStream(bmp, writable: false);
        using var bitmap = DecodeUnpremultiplied(input);
        using var image = SKImage.FromBitmap(bitmap);
        using var output = image.Encode(SKEncodedImageFormat.Png, 100);
        if (output is null || output.Size > PngLimit) throw new IOException("Clipboard image exceeds PNG limit.");
        return output.ToArray();
    }

    private static SKBitmap DecodeUnpremultiplied(Stream stream)
    {
        using var codec = SKCodec.Create(stream) ?? throw new IOException("Invalid native clipboard image.");
        CheckDimensions(codec.Info.Width, codec.Info.Height);
        // The normal UI bitmap pipeline premultiplies alpha. Explicit unpremultiplied decode
        // avoids quantizing semitransparent RGB when converting between native DIB and PNG.
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) == SKCodecResult.Success) return bitmap;
        bitmap.Dispose(); throw new IOException("Native clipboard image decoding failed.");
    }

    private static void ValidateContent(SharedClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content.Data);
        if (content.Kind == "text")
        {
            if (content.Data.Length > TextLimit || content.Data.AsSpan().Contains((byte)0)) throw new ArgumentException("Invalid clipboard text.");
            _ = new UTF8Encoding(false, true).GetString(content.Data);
        }
        else if (content.Kind == "png") ValidatePng(content.Data);
        else throw new ArgumentException("Unsupported clipboard content.");
    }

    private static void ValidatePng(byte[] bytes)
    {
        if (bytes.Length is < 24 or > PngLimit || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new IOException("Invalid clipboard PNG.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20));
        if (width > int.MaxValue || height > int.MaxValue) throw new IOException("Invalid clipboard PNG dimensions.");
        CheckDimensions((int)width, (int)height);
    }

    private static byte[] TrimPng(byte[] bytes)
    {
        ValidatePng(bytes);
        var offset = 8;
        while (offset <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            if (length > bytes.Length - offset - 12) throw new IOException("Truncated clipboard PNG.");
            var end = checked(offset + 12 + (int)length);
            if (bytes.AsSpan(offset + 4, 4).SequenceEqual("IEND"u8))
                return end == bytes.Length ? bytes : bytes.AsSpan(0, end).ToArray();
            offset = end;
        }
        throw new IOException("Clipboard PNG has no end marker.");
    }

    private static void CheckDimensions(int width, int height)
    {
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 || (long)width * height > PixelLimit)
            throw new IOException("Clipboard image dimensions exceed limits.");
    }

    private static byte[] ReadGlobal(IntPtr handle, int limit)
    {
        if (handle == IntPtr.Zero) throw new IOException("Clipboard data is not available.");
        var size = GlobalSize(handle).ToUInt64();
        if (size == 0 || size > (ulong)limit) throw new IOException("Clipboard data exceeds limits.");
        var address = GlobalLock(handle);
        if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { var bytes = new byte[(int)size]; Marshal.Copy(address, bytes, 0, bytes.Length); return bytes; }
        finally { GlobalUnlock(handle); }
    }

    private static void WriteGlobal(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(2, (UIntPtr)bytes.Length); // GMEM_MOVEABLE: ownership transfers to the system.
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var address = GlobalLock(handle);
            if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, address, bytes.Length); }
            finally { GlobalUnlock(handle); }
            if (SetClipboardData(format, handle) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            handle = IntPtr.Zero;
        }
        finally { if (handle != IntPtr.Zero) GlobalFree(handle); }
    }

    private void EmitDiagnostic(string text) { try { Diagnostic?.Invoke(text); } catch { } }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var hwnd = _window;
        if (hwnd != IntPtr.Zero) PostMessageW(hwnd, WmClose, UIntPtr.Zero, IntPtr.Zero);
        if (_thread is not null && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        GC.KeepAlive(_windowProc);
    }

    private sealed class ApplyRequest(SharedClipboardContent content, CancellationToken token)
    {
        public SharedClipboardContent Content { get; } = content;
        public CancellationToken Token { get; } = token;
        public long Enqueued { get; } = Environment.TickCount64;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(uint Format, byte[] Data)>? Prepared { get; set; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public WindowProc Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public IntPtr Hwnd; public uint Kind; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Point; public uint Private;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? module);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint extended, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint delay, IntPtr callback);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormatW(string name);
}
