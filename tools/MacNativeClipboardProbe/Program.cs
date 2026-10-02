using System.Runtime.InteropServices;
using System.Text;
using LinkBridge.App.Services;

if (!OperatingSystem.IsMacOS() || args.Length != 2) throw new ArgumentException("Use on Mac with PNG and TIFF fixture paths.");
NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
var pool = Probe.Send(Probe.Send(Probe.Class("NSAutoreleasePool"), Probe.Selector("alloc")), Probe.Selector("init"));
var board = Probe.Send(Probe.Class("NSPasteboard"), Probe.Selector("pasteboardWithUniqueName"));
try
{
    var marker = Encoding.UTF8.GetBytes("LinkBridge native fixture 中文 🐈\nsecond line");
    MacSharedClipboard.ApplyNative(new("text", marker), board);
    var text = MacSharedClipboard.ReadNative(board);
    if (text?.Kind != "text" || !text.Data.SequenceEqual(marker)) throw new IOException("Text roundtrip failed.");
    var png = File.ReadAllBytes(args[0]);
    MacSharedClipboard.ApplyNative(new("png", png), board);
    var readPng = MacSharedClipboard.ReadNative(board);
    if (readPng?.Kind != "png" || !readPng.Data.SequenceEqual(png)) throw new IOException("Native PNG bytes changed.");
    Probe.SetData(board, "public.tiff", File.ReadAllBytes(args[1]));
    var converted = MacSharedClipboard.ReadNative(board);
    if (converted?.Kind != "png" || converted.Data.Length < 8 || !converted.Data.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))
        throw new IOException("TIFF conversion did not produce PNG.");
    File.WriteAllBytes(args[0] + ".converted.png", converted.Data);
    Probe.SetData(board, "public.file-url", Encoding.UTF8.GetBytes("file:///private/not-exported"));
    Probe.SetData(board, "public.utf8-plain-text", marker, false);
    Probe.SetData(board, "public.png", png, false);
    if (MacSharedClipboard.ReadNative(board) is not null) throw new IOException("File path was exported.");
    Probe.SetData(board, "NSFilenamesPboardType", Encoding.UTF8.GetBytes("isolated legacy fixture"));
    Probe.SetData(board, "public.utf8-plain-text", marker, false);
    if (MacSharedClipboard.ReadNative(board) is not null) throw new IOException("Legacy file alias was exported.");
    Console.WriteLine("PASS private NSPasteboard Unicode text exact roundtrip, native PNG byte preservation, TIFF-to-PNG, file-path exclusion; general user clipboard untouched.");
}
finally { Probe.Send(board, Probe.Selector("releaseGlobally")); Probe.Send(pool, Probe.Selector("drain")); }

namespace LinkBridge.App.Services { public sealed record SharedClipboardContent(string Kind, byte[] Data); }
internal static class Probe
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC,EntryPoint="objc_getClass")] internal static extern nint Class([MarshalAs(UnmanagedType.LPUTF8Str)]string name);
    [DllImport(ObjC,EntryPoint="sel_registerName")] internal static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)]string name);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] internal static extern nint Send(nint target,nint selector);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] private static extern nint SendString(nint target,nint selector,[MarshalAs(UnmanagedType.LPUTF8Str)]string value);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] private static extern nint SendData(nint target,nint selector,nint data,nuint count);
    [DllImport(ObjC,EntryPoint="objc_msgSend")] [return:MarshalAs(UnmanagedType.I1)] private static extern bool SendBool2(nint target,nint selector,nint data,nint type);
    internal static void SetData(nint board,string type,byte[] data,bool clear = true)
    {
        var pin=GCHandle.Alloc(data,GCHandleType.Pinned);
        try
        {
            var bytes=SendData(Class("NSData"),Selector("dataWithBytes:length:"),pin.AddrOfPinnedObject(),(nuint)data.Length);
            var text=SendString(Class("NSString"),Selector("stringWithUTF8String:"),type);
            if(clear)Send(board,Selector("clearContents"));
            if(!SendBool2(board,Selector("setData:forType:"),bytes,text))throw new IOException("Private pasteboard fixture write failed.");
        }
        finally {pin.Free();}
    }
}
