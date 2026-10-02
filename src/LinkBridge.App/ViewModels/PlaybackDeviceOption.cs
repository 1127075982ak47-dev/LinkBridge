using LinkBridge.Core;
using LinkBridge.Core.Models;

namespace LinkBridge.App.ViewModels;

public sealed record PlaybackDeviceOption(string Id, string Name, bool IsDefault, bool IsAvailable = true, string? Owner = null)
{
    public string Label => Name;
    public string Details => Id.Length == 0 ? "系统切换输出时自动跟随" :
        $"{Owner ?? PlatformInfo.DisplayName + " · " + PlatformInfo.MachineName} · {(IsAvailable ? IsDefault ? "系统默认输出" : "可选输出" : "设备已离线")}";
    public static PlaybackDeviceOption From(MediaDevice device) => new(device.Id, device.Name, device.IsDefaultPlayback);
}
