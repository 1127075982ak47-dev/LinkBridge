using LinkBridge.Core.Models;

namespace LinkBridge.Media;

/// <summary>
/// 枚举系统音频端点。
/// ffmpeg 的 DirectShow 只能看到「输入」设备（摄像头、麦克风），
/// 音响这类「输出」端点它根本枚举不到，所以必须单独走系统音频接口补齐。
/// </summary>
public static class AudioEndpointCatalog
{
    /// <summary>上一次枚举失败的原因，供界面显示排查。</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 列出本机的「音响」（输出端点）。麦克风不从这里出 ——
    /// 它的 Id 必须来自 ffmpeg 认识的 dshow / avfoundation 设备名（见 MediaDeviceCatalog）。
    /// </summary>
    public static IReadOnlyList<MediaDevice> Enumerate()
    {
        LastError = null;

        if (OperatingSystem.IsMacOS())
        {
            // macOS：CoreAudio（AudioObject API），Id = 端点 UID
            var speakers = CoreAudioEndpoints.EnumerateSpeakers(out var macError);
            LastError = macError;
            return speakers;
        }

        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var result = new List<MediaDevice>();

        try
        {
            result.AddRange(WindowsAudioEndpointQueries.EnumerateSpeakers());
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        return result;
    }
}
