namespace LinkBridge.Core.Models;

/// <summary>可供跨机共享的音视频设备类型。</summary>
public enum MediaDeviceKind
{
    /// <summary>相机 / 摄像头。</summary>
    Camera,

    /// <summary>麦克风。</summary>
    Microphone,

    /// <summary>音响 / 扬声器（系统声音回环，暂未实现）。</summary>
    Speaker,
}

/// <summary>设备来自哪套后端，决定 <see cref="MediaDevice.Id"/> 的语义。</summary>
public enum MediaBackend
{
    /// <summary>Windows DirectShow（ffmpeg 采集用）。Id = 设备名。</summary>
    DirectShow,

    /// <summary>macOS AVFoundation（ffmpeg 采集用）。Id = 设备名。</summary>
    AvFoundation,

    /// <summary>
    /// 系统音频接口（Windows / macOS 的 CoreAudio）。
    /// 只用于音响这类「输出端点」，Id = 端点的稳定唯一 ID（不是显示名，显示名会随系统语言变）。
    /// </summary>
    CoreAudio,
}

/// <summary>一个被发现的本地设备。</summary>
/// <param name="Kind">设备类型。</param>
/// <param name="Id">平台内部标识，具体语义由 <paramref name="Backend"/> 决定。</param>
/// <param name="Name">给人看的名字。</param>
/// <param name="Backend">来自哪套后端。</param>
public sealed record MediaDevice(MediaDeviceKind Kind, string Id, string Name, MediaBackend Backend)
{
    public bool IsDefaultPlayback { get; init; }
    public bool IsApplicationEndpoint { get; init; }

    public string KindLabel => Kind switch
    {
        MediaDeviceKind.Camera => "相机",
        MediaDeviceKind.Microphone => "麦克风",
        MediaDeviceKind.Speaker => "音响",
        _ => "设备",
    };
}
