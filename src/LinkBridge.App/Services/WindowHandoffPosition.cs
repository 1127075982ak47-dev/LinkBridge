using System.Runtime.InteropServices;

namespace LinkBridge.App.Services;

internal static class WindowHandoffPosition
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MacPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MacSize { public double Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct MacRect { public MacPoint Origin; public MacSize Size; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private const string Quartz = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(Quartz)] private static extern nint CGEventCreate(nint source);
    [DllImport(Quartz)] private static extern MacPoint CGEventGetLocation(nint evt);
    [DllImport(Quartz)] private static extern uint CGMainDisplayID();
    [DllImport(Quartz)] private static extern MacRect CGDisplayBounds(uint display);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint value);

    public static double ReadNormalizedY()
    {
        if (OperatingSystem.IsWindows() && GetCursorPos(out var point))
            return Math.Clamp(point.Y / (double)Math.Max(1, GetSystemMetrics(1) - 1), 0, 1);
        if (OperatingSystem.IsMacOS())
        {
            var evt = CGEventCreate(0);
            if (evt != 0)
            {
                try
                {
                    var bounds = CGDisplayBounds(CGMainDisplayID());
                    return Math.Clamp((CGEventGetLocation(evt).Y - bounds.Origin.Y) / Math.Max(1, bounds.Size.Height - 1), 0, 1);
                }
                finally { CFRelease(evt); }
            }
        }
        return .5;
    }
}
