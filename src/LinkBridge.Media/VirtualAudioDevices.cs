namespace LinkBridge.Media;

/// <summary>
/// 虚拟音频回环设备（BlackHole / Soundflower / VB-Cable 之类）的识别。
///
/// 这类设备会以「麦克风」身份同时出现在 avfoundation / CoreAudio 的输入列表里，
/// 也可能以「音响」身份出现在输出列表里。但它不是真设备：
/// 共享出去要么是静音，要么是自我回环，界面上必须剔掉，否则会多出假的设备项。
/// </summary>
internal static class VirtualAudioDevices
{
    private static readonly string[] Markers =
    [
        "BlackHole",
        "Soundflower",
        "Loopback",
        "VB-Cable",
        "VB-Audio",
        "Virtual Audio",
    ];

    /// <summary>名字或 UID 命中已知虚拟回环设备名时返回 true。</summary>
    public static bool IsLoopback(string? name, string? uid)
    {
        var haystack = string.Concat(name ?? string.Empty, " ", uid ?? string.Empty);

        foreach (var marker in Markers)
        {
            if (haystack.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
