using System.Text.RegularExpressions;
using LinkBridge.Core;
using LinkBridge.Core.Models;

namespace LinkBridge.Media;

/// <summary>枚举本机可供共享的相机与麦克风。底层借用 ffmpeg 的设备列举能力。</summary>
public sealed partial class MediaDeviceCatalog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>列出本机设备。ffmpeg 缺失时返回空列表。</summary>
    public async Task<IReadOnlyList<MediaDevice>> EnumerateAsync(CancellationToken ct = default)
    {
        var ffmpeg = FfmpegLocator.ExecutablePath;
        if (ffmpeg is null)
        {
            return [];
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var output = await ProcessHelper.RunAndCaptureAsync(
                ffmpeg,
                PlatformInfo.IsWindows
                    ? ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"]
                    : ["-hide_banner", "-list_devices", "true", "-f", "avfoundation", "-i", ""],
                TimeSpan.FromSeconds(20),
                ct).ConfigureAwait(false);

            var devices = PlatformInfo.IsWindows ? ParseDirectShow(output) : ParseAvFoundation(output);

            // ffmpeg 的 DirectShow 只能看到输入设备，音响这类输出端点要另外补上（按名字去重）
            foreach (var extra in AudioEndpointCatalog.Enumerate())
            {
                var duplicated = devices.Any(d =>
                    d.Kind == extra.Kind &&
                    string.Equals(d.Name, extra.Name, StringComparison.OrdinalIgnoreCase));

                if (!duplicated)
                {
                    devices.Add(extra);
                }
            }

            return devices
                .OrderBy(d => d.Kind)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>解析 Windows DirectShow 设备列表。</summary>
    private static List<MediaDevice> ParseDirectShow(string output)
    {
        var result = new List<MediaDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in DirectShowRegex().Matches(output))
        {
            var name = match.Groups["name"].Value.Trim();
            var kind = match.Groups["kind"].Value == "video"
                ? MediaDeviceKind.Camera
                : MediaDeviceKind.Microphone;

            if (name.Length == 0 || !seen.Add(kind + "\u0001" + name))
            {
                continue;
            }

            result.Add(new MediaDevice(kind, name, name, MediaBackend.DirectShow)
            { IsApplicationEndpoint = kind == MediaDeviceKind.Camera && name.StartsWith("联桥共享摄像头", StringComparison.Ordinal) });
        }

        return result;
    }

    /// <summary>解析 macOS AVFoundation 设备列表（形如 [0] 设备名）。</summary>
    private static List<MediaDevice> ParseAvFoundation(string output)
    {
        var result = new List<MediaDevice>();
        var section = MediaDeviceKind.Camera;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Contains("video devices", StringComparison.OrdinalIgnoreCase))
            {
                section = MediaDeviceKind.Camera;
                continue;
            }

            if (line.Contains("audio devices", StringComparison.OrdinalIgnoreCase))
            {
                section = MediaDeviceKind.Microphone;
                continue;
            }

            var match = AvFoundationRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var index = match.Groups["idx"].Value;
            var name = match.Groups["name"].Value.Trim();

            // 屏幕捕获不是我们要共享的"相机"
            if (section == MediaDeviceKind.Camera && name.StartsWith("Capture screen", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 虚拟回环设备（BlackHole / Soundflower 之类）也会以「麦克风」身份出现在
            // avfoundation 列表里，但它不是真麦克风（共享出去大概率是静音），必须剔掉，
            // 否则装完 BlackHole 后界面上会多出一个假麦克风。
            if (section == MediaDeviceKind.Microphone && VirtualAudioDevices.IsLoopback(name, null))
            {
                continue;
            }

            // AVFoundation 的输入串是「视频索引:音频索引」。名称可能包含冒号，
            // 也可能被设备驱动改写；使用本次枚举得到的索引才能稳定打开。
            result.Add(new MediaDevice(section, index, name, MediaBackend.AvFoundation));
        }

        return result;
    }

    [GeneratedRegex("\"(?<name>[^\"]+)\"\\s+\\((?<kind>video|audio)\\)")]
    private static partial Regex DirectShowRegex();

    [GeneratedRegex("\\[(?<idx>\\d+)\\]\\s+(?<name>.+)$")]
    private static partial Regex AvFoundationRegex();
}
