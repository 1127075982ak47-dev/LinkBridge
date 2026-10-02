using NAudio.CoreAudioApi;

namespace LinkBridge.Media;

/// <summary>读取/修改选定实体输出的系统音量，空 ID 跟随系统默认。</summary>
public static class PlaybackVolumeController
{
    public static double? Read(string id)
    {
        if (OperatingSystem.IsMacOS()) return CoreAudioEndpoints.ReadVolume(id);
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return WindowsAudioEndpointQueries.ReadVolume(id);
        }
        catch { return null; }
    }

    public static void Write(string id, double percent)
    {
        if (OperatingSystem.IsMacOS()) { CoreAudioEndpoints.WriteVolume(id, percent); return; }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("该平台的实体音量控制尚未接入。");
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = id.Length == 0
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(id);
        endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(Math.Clamp(percent, 0, 100) / 100);
        if (percent > 0 && endpoint.AudioEndpointVolume.Mute) endpoint.AudioEndpointVolume.Mute = false;
    }

    public static bool CanWrite(string id)
    {
        if (OperatingSystem.IsMacOS()) return CoreAudioEndpoints.CanWriteVolume(id);
        return OperatingSystem.IsWindows() && Read(id).HasValue;
    }
}
