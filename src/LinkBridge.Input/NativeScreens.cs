using System.Runtime.InteropServices;
using LinkBridge.Core.DisplayLayout;

namespace LinkBridge.Input;

public static class NativeScreens
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public Rect Monitor,Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorCallback callback,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [StructLayout(LayoutKind.Sequential)] private struct CGPoint {public double X,Y;}
    [StructLayout(LayoutKind.Sequential)] private struct CGSize {public double Width,Height;}
    [StructLayout(LayoutKind.Sequential)] private struct CGRect {public CGPoint Origin; public CGSize Size;}
    private const string Quartz="/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(Quartz)] private static extern int CGGetActiveDisplayList(uint count,[Out] uint[] displays,out uint actual);
    [DllImport(Quartz)] private static extern uint CGMainDisplayID();
    [DllImport(Quartz)] private static extern CGRect CGDisplayBounds(uint display);
    public static ScreenInfo[] Read()
    {
        var result=new List<ScreenInfo>();
        if(OperatingSystem.IsWindows()) {
            var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
            try {
                EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,(IntPtr m,IntPtr dc,ref Rect r,IntPtr data)=>{
                    var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
                    if(GetMonitorInfoW(m,ref info)) { var b=info.Monitor;
                        result.Add(new(info.Device,$"Windows 屏 {result.Count+1}",b.Left,b.Top,b.Right-b.Left,b.Bottom-b.Top,(info.Flags&1)!=0)); }
                    return true;
                },IntPtr.Zero);
            } finally {if(previous!=IntPtr.Zero)SetThreadDpiAwarenessContext(previous);}
        } else if(OperatingSystem.IsMacOS()) {
            var ids=new uint[16];if(CGGetActiveDisplayList(16,ids,out var count)!=0)throw new IOException("读取 Mac 屏幕失败。");
            for(int i=0;i<count;i++){var b=CGDisplayBounds(ids[i]);result.Add(new($"CG-{ids[i]}",$"Mac 屏 {i+1}",(int)b.Origin.X,(int)b.Origin.Y,(int)b.Size.Width,(int)b.Size.Height,ids[i]==CGMainDisplayID()));}
        }
        return result.OrderBy(s=>s.X).ThenBy(s=>s.Y).ToArray();
    }
}
