using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LinkBridge.App.Services;
using SkiaSharp;

internal static class Program
{
    private static readonly List<SharedClipboardContent> Observed = [];
    [STAThread]
    public static void Main()
    {
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        using var adapter = new WindowsSharedClipboard();
        adapter.Changed += item => { lock (Observed) Observed.Add(item); };
        adapter.Diagnostic += message => Console.WriteLine("DIAGNOSTIC " + message);
        adapter.Start();
        Thread.Sleep(600);
        Assert(Count == 0, "startup history skipped");
        adapter.ApplyAsync(new("text", Encoding.UTF8.GetBytes("LinkBridge native clipboard self-write"))).GetAwaiter().GetResult();
        Thread.Sleep(600);
        Assert(Count == 0, "remote-apply echo suppressed");
        using var external = new WindowsSharedClipboard(); external.Start();
        var utf8 = Encoding.UTF8.GetBytes("LinkBridge 原生文本 🐈\r\nsecond line");
        external.ApplyAsync(new("text", utf8)).GetAwaiter().GetResult();
        WaitCount(1);
        Assert(Observed[0].Kind == "text" && Observed[0].Data.SequenceEqual(utf8), "external native Unicode text observed exactly");
        var png = CreatePng();
        external.ApplyAsync(new("png", png)).GetAwaiter().GetResult();
        WaitCount(2);
        Assert(Observed[1].Kind == "png" && Observed[1].Data.SequenceEqual(png), "external native PNG bytes unchanged");
        Assert(IsClipboardFormatAvailable(8) && IsClipboardFormatAvailable(17), "standard DIB and DIBV5 exposed for native app paste");
        var owner = (IntPtr)typeof(WindowsSharedClipboard).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(external)!;
        var dibV5 = ReadClipboard(17);
        var dib = ReadClipboard(8);
        dibV5[124] = 0xEE; // Change a pixel to exercise native bitmap conversion independently.
        WriteClipboard(owner, (17, dibV5)); // Simulate a native bitmap producer without custom PNG.
        WaitCount(3);
        using var native = new Bitmap(new MemoryStream(Observed[2].Data));
        Assert(native.PixelSize == new PixelSize(17, 9), "DIB-only native producer kept original dimensions");
        var expectedPixels = OriginalPixels(); expectedPixels[0] = 0xEE;
        Assert(Pixels(Observed[2].Data).SequenceEqual(expectedPixels), "DIBV5 alpha/RGB round trip exact");
        WriteClipboard(owner, (8, dib));
        WaitCount(4);
        var legacyPixels = OriginalPixels();
        for (var pixel = 0; pixel < 17 * 9; pixel++) legacyPixels[pixel * 4 + 3] = 255;
        Assert(Pixels(Observed[3].Data).SequenceEqual(legacyPixels), "legacy DIB RGB and native opaque semantics exact");
        var hdrop = new byte[24]; // DROPFILES header + double UTF-16 NUL, ignored regardless of alternate text.
        hdrop[0] = 20; hdrop[16] = 1;
        WriteClipboard(owner, (15, hdrop), (13, Encoding.Unicode.GetBytes("H:\\do-not-send-file-path.bin\0")));
        Thread.Sleep(650);
        Assert(Count == 4, "CF_HDROP alternate path text not sent");
        external.ApplyAsync(new("png", png)).GetAwaiter().GetResult();
        WaitCount(5);
        Assert(Observed[4].Data.SequenceEqual(png), "A -> unsupported file clipboard -> A emits again");
        external.ApplyAsync(new("png", png)).GetAwaiter().GetResult();
        WaitCount(6);
        Assert(Observed[5].Data.SequenceEqual(png), "intentional repeat copy emits again");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { adapter.ApplyAsync(new("text", utf8), cancelled.Token).GetAwaiter().GetResult(); throw new Exception("cancel ignored"); }
        catch (OperationCanceledException) { Console.WriteLine("PASS cancelled receive does not write clipboard"); }
        external.ApplyAsync(new("text", Encoding.UTF8.GetBytes("LinkBridge clipboard probe completed"))).GetAwaiter().GetResult();
        Console.WriteLine("PASS native Windows clipboard adapter (no GUI automation, no clipboard contents logged)");
    }
    private static int Count { get { lock (Observed) return Observed.Count; } }
    private static void WaitCount(int expected)
    {
        var limit = Environment.TickCount64 + 4000;
        while (Count < expected && Environment.TickCount64 < limit) Thread.Sleep(20);
        Assert(Count == expected, "native clipboard change delivered " + expected);
    }
    private static void Assert(bool ok, string caption) { if (!ok) throw new Exception("FAIL " + caption); Console.WriteLine("PASS " + caption); }
    private static byte[] OriginalPixels()
    {
        var data = new byte[17 * 9 * 4];
        for (var pixel = 0; pixel < 17 * 9; pixel++)
        { data[pixel * 4] = (byte)(pixel * 7); data[pixel * 4 + 1] = (byte)(pixel * 11); data[pixel * 4 + 2] = (byte)(pixel * 17); data[pixel * 4 + 3] = (byte)(64 + pixel % 192); }
        return data;
    }
    private static byte[] CreatePng()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(17, 9), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var frame = bitmap.Lock()) Marshal.Copy(OriginalPixels(), 0, frame.Address, 17 * 9 * 4);
        using var output = new MemoryStream(); bitmap.Save(output, new PngBitmapEncoderOptions()); return output.ToArray();
    }
    private static byte[] Pixels(byte[] png)
    {
        using var codec = SKCodec.Create(new MemoryStream(png));
        var info = new SKImageInfo(17, 9, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var pixels = new SKBitmap(info);
        if (codec.GetPixels(info, pixels.GetPixels()) != SKCodecResult.Success) throw new Exception("Invalid probe PNG");
        var data = new byte[17 * 9 * 4];
        for (var row = 0; row < 9; row++) Marshal.Copy(IntPtr.Add(pixels.GetPixels(), row * pixels.RowBytes), data, row * 17 * 4, 17 * 4);
        return data;
    }
    private static byte[] ReadClipboard(uint format)
    {
        if (!OpenClipboard(IntPtr.Zero)) throw new Exception("clipboard busy");
        try
        {
            var handle = GetClipboardData(format); var data = new byte[(int)GlobalSize(handle)]; var address = GlobalLock(handle);
            try { Marshal.Copy(address, data, 0, data.Length); return data; } finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }
    private static void WriteClipboard(IntPtr owner, params (uint Format, byte[] Data)[] formats)
    {
        if (!OpenClipboard(owner)) throw new Exception("clipboard busy");
        try
        {
            if (!EmptyClipboard()) throw new Exception("empty clipboard failed");
            foreach (var (format, data) in formats)
            {
                var handle = GlobalAlloc(2, (UIntPtr)data.Length); var address = GlobalLock(handle);
                Marshal.Copy(data, 0, address, data.Length); GlobalUnlock(handle);
                if (SetClipboardData(format, handle) == IntPtr.Zero) throw new Exception("set clipboard failed");
            }
        }
        finally { CloseClipboard(); }
    }
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
}
