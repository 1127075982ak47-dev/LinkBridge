namespace LinkBridge.Core;

/// <summary>运行平台判定，供各模块做平台分支。</summary>
public static class PlatformInfo
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool IsMacOS => OperatingSystem.IsMacOS();

    public static string DisplayName => IsWindows ? "Windows" : IsMacOS ? "macOS" : "Linux";

    /// <summary>本机名，用于在局域网上标识自己。</summary>
    public static string MachineName => Environment.MachineName;
}
