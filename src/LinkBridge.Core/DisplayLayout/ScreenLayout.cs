using System.Text.Json;

namespace LinkBridge.Core.DisplayLayout;

public sealed record ScreenInfo(string Id, string Name, int X, int Y, int Width, int Height, bool Primary, double Scale = 1);
public sealed record ScreenLayout
{
    public int Version { get; init; } = 1;
    public string WindowsScreenId { get; init; } = "";
    public string MacScreenId { get; init; } = "";
    public string Edge { get; init; } = "bottom";
    public int Offset { get; init; } = 320;
    public int CoveragePercent { get; init; } = 100;
    public int SwitchDelayMs { get; init; } = 150;
    public string WindowsDisplaySignature { get; init; } = "";
    public string MacDisplaySignature { get; init; } = "";
}
public sealed record LayoutSnapshot(string Platform, string Name, ScreenInfo[] Screens, ScreenLayout? Layout, string Status = "");

/// <summary>Coordinates are native input coordinates: Windows pixels, Mac CoreGraphics points.</summary>
public sealed record ScreenPortal(ScreenInfo Local, ScreenInfo Remote, string Edge, int Start, int End, int TargetStart, int DelayMs)
{
    public bool Horizontal => Edge is "bottom" or "top";
    public bool AtEdge(int x, int y)
    {
        int along = Horizontal ? x : y;
        if (along < Start || along >= End) return false;
        return Edge switch {
            "bottom" => y >= Local.Y + Local.Height - 2 && y < Local.Y + Local.Height + 2,
            "top" => y <= Local.Y + 1 && y >= Local.Y - 2,
            "right" => x >= Local.X + Local.Width - 2 && x < Local.X + Local.Width + 2,
            _ => x <= Local.X + 1 && x >= Local.X - 2 };
    }
    public bool Outward(int dx, int dy) => Edge switch { "bottom" => dy > 0, "top" => dy < 0, "right" => dx > 0, _ => dx < 0 };
    public bool Returns(int x, int y, int dx, int dy)
    {
        int along = Horizontal ? Math.Clamp(x+dx,0,Remote.Width-1) : Math.Clamp(y+dy,0,Remote.Height-1);
        if(along < TargetStart || along >= TargetStart+End-Start)return false;
        return Edge switch {
            "bottom" => dy < 0 && y + dy <= 0, "top" => dy > 0 && y + dy >= Remote.Height - 1,
            "right" => dx < 0 && x + dx <= 0, _ => dx > 0 && x + dx >= Remote.Width - 1 };
    }
    public (int X,int Y) Enter(int x,int y)
    {
        var along = Math.Clamp(TargetStart + (Horizontal ? x : y) - Start, 0, (Horizontal ? Remote.Width : Remote.Height) - 1);
        return Edge switch { "bottom" => (along, 24), "top" => (along, Remote.Height - 25),
            "right" => (24, along), _ => (Remote.Width - 25, along) };
    }
    public (int X,int Y) Leave(int x,int y)
    {
        var along = Start + Math.Clamp((Horizontal ? x : y) - TargetStart, 0, End - Start - 1);
        return Edge switch { "bottom" => (along, Local.Y + Local.Height - 32), "top" => (along, Local.Y + 32),
            "right" => (Local.X + Local.Width - 32, along), _ => (Local.X + 32, along) };
    }
    public static ScreenPortal Create(ScreenLayout layout, IReadOnlyList<ScreenInfo> windows, IReadOnlyList<ScreenInfo> mac)
    {
        if (layout.Version != 1 || layout.Edge is not ("left" or "right" or "top" or "bottom") ||
            layout.CoveragePercent is < 20 or > 100 || layout.SwitchDelayMs is < 0 or > 2000 || Math.Abs((long)layout.Offset) > 65536)
            throw new InvalidDataException("布局参数无效，请重新排列屏幕。");
        var local = windows.SingleOrDefault(s => s.Id == layout.WindowsScreenId) ?? throw new InvalidDataException("原来的 Windows 屏幕已断开，请重新选择屏幕。");
        var remote = mac.SingleOrDefault(s => s.Id == layout.MacScreenId) ?? throw new InvalidDataException("原来的 Mac 屏幕已断开，请重新选择屏幕。");
        foreach (var s in new[] {local, remote})
            if (s.Width is < 320 or > 32768 || s.Height is < 200 or > 32768 || Math.Abs((long)s.X) > 65536 || Math.Abs((long)s.Y) > 65536)
                throw new InvalidDataException("屏幕范围无效，请刷新屏幕。");
        bool horizontal = layout.Edge is "bottom" or "top";
        int origin = horizontal ? local.X : local.Y, length = horizontal ? local.Width : local.Height;
        int remoteLength = horizontal ? remote.Width : remote.Height;
        int start = Math.Max(origin, origin + layout.Offset), end = Math.Min(origin + length, origin + layout.Offset + remoteLength);
        if (end - start < 64) throw new InvalidDataException("两块屏幕没有足够的接触边缘，请把 Mac 卡片拖近 Windows 屏幕。");
        int trim = (end - start) * (100 - layout.CoveragePercent) / 200; start += trim; end -= trim;
        foreach (var other in windows.Where(s => s.Id != local.Id))
        {
            int rx = layout.Edge switch { "left" => local.X-remote.Width, "right" => local.X+local.Width, _ => local.X+layout.Offset };
            int ry = layout.Edge switch { "top" => local.Y-remote.Height, "bottom" => local.Y+local.Height, _ => local.Y+layout.Offset };
            if (Math.Max(rx,other.X) < Math.Min(rx+remote.Width,other.X+other.Width) &&
                Math.Max(ry,other.Y) < Math.Min(ry+remote.Height,other.Y+other.Height))
                throw new InvalidDataException("Mac 卡片与 Windows 屏幕重叠，请拖到桌面的外侧边缘。");
            bool blocks = layout.Edge switch {
                "bottom" => other.Y <= local.Y + local.Height && other.Y + other.Height > local.Y + local.Height,
                "top" => other.Y < local.Y && other.Y + other.Height >= local.Y,
                "right" => other.X <= local.X + local.Width && other.X + other.Width > local.X + local.Width,
                _ => other.X < local.X && other.X + other.Width >= local.X };
            int a = horizontal ? other.X : other.Y, b = a + (horizontal ? other.Width : other.Height);
            if (blocks && Math.Max(start, a) < Math.Min(end, b))
                throw new InvalidDataException("这里连接着另一块 Windows 屏幕。请把 Mac 放到 Windows 桌面的外侧边缘。");
        }
        return new(local, remote, layout.Edge, start, end, start - origin - layout.Offset, layout.SwitchDelayMs);
    }
}

public static class ScreenLayoutStore
{
    public static string Signature(ScreenInfo[] screens) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(screens)));
    public static string FilePath => Path.Combine(Path.GetDirectoryName(SettingsStore.FilePath)!, "screen-layout.json");
    public static ScreenLayout? Load(bool previous = false)
    {
        var path = FilePath + (previous ? ".previous" : "");
        return File.Exists(path) ? JsonSerializer.Deserialize<ScreenLayout>(File.ReadAllText(path)) : null;
    }
    public static void Save(ScreenLayout layout)
    {
        var path = FilePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(layout, new JsonSerializerOptions {WriteIndented = true}));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (File.Exists(path)) File.Copy(path, path + ".previous", true);
        File.Move(temp, path, true);
    }
}
