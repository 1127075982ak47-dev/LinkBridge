using System.Diagnostics;
using LinkBridge.Core;
using LinkBridge.Core.Models;

namespace LinkBridge.Media;

/// <summary>一次媒体共享会话：把选中的相机/麦克风编码后推到对端。</summary>
public sealed class MediaStreamSession : IDisposable
{
    private readonly object _gate = new();
    private readonly object _lifecycleGate = new();
    private readonly Func<ProcessStartInfo, Process> _processFactory;
    private Process? _process;
    private bool _disposed, _ended;
    private long _generation;
    private string? _target;

    public MediaStreamSession() : this(info => new Process { StartInfo = info }) { }

    // Isolated probes can supply synthetic FFmpeg inputs without touching devices.
    internal MediaStreamSession(Func<ProcessStartInfo, Process> processFactory) =>
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));

    /// <summary>ffmpeg 的实时输出，供界面显示。</summary>
    public event Action<string>? LogEmitted;
    public event Action? StreamEnded;

    public bool IsRunning
    {
        get { lock (_gate) { try { return !_disposed && _process is { HasExited: false }; } catch { return false; } } }
    }

    /// <summary>当前推流目标，形如 192.0.2.20:12345。</summary>
    public string? Target { get { lock (_gate) return _target; } }

    /// <summary>开始推流。相机和麦克风至少要有一个。</summary>
    public void Start(
        MediaDevice? camera,
        MediaDevice? microphone,
        string targetHost,
        int targetPort,
        AppSettings settings)
    {
        lock (_lifecycleGate)
        {
            Process? previous;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ArgumentNullException.ThrowIfNull(settings);
                targetHost = ValidateHost(targetHost);
                if (targetPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(targetPort));
                if (camera is null && microphone is null)
                    throw new InvalidOperationException("至少需要选择一个相机或麦克风。");
                previous = DetachLocked();
            }
            // Process.Dispose may wait for asynchronous pipe/exit callbacks. Those
            // callbacks use _gate, so disposal must run outside the state lock.
            Retire(previous);
            var ffmpeg = FfmpegLocator.ExecutablePath
                ?? throw new InvalidOperationException("本机没有找到 ffmpeg，无法启动共享。");
            var info = new ProcessStartInfo
            {
                FileName = ffmpeg,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var argument in BuildArguments(camera, microphone, targetHost, targetPort, settings))
                info.ArgumentList.Add(argument);
            var process = _processFactory(info);
            process.EnableRaisingEvents = true;
            long generation;
            lock (_gate) generation = ++_generation;
            void Exited()
            {
                lock (_gate)
                {
                    if (!IsCurrent(process, generation) || _ended) return;
                    _ended = true; _target = null;
                    LogEmitted?.Invoke($"[错误] 推流进程已退出，退出码 {process.ExitCode}");
                    if (IsCurrent(process, generation)) StreamEnded?.Invoke();
                }
            }
            process.Exited += (_, _) => Exited();
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (_gate) if (IsCurrent(process, generation)) LogEmitted?.Invoke(e.Data);
            };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("ffmpeg 启动失败。");
                lock (_gate)
                {
                    _process = process; _ended = false;
                    _target = $"{targetHost}:{targetPort}";
                }
                process.BeginErrorReadLine();
                lock (_gate)
                    if (IsCurrent(process, generation)) LogEmitted?.Invoke($"[LinkBridge] 已开始共享 → {_target}");
                // An immediate failure can exit before its Process has been published.
                if (process.HasExited) Exited();
            }
            catch
            {
                lock (_gate) if (ReferenceEquals(_process, process)) DetachLocked();
                Retire(process); throw;
            }
        }
    }

    /// <summary>停止推流。退役回调不会改变新一代会话。</summary>
    public void Stop()
    {
        lock (_lifecycleGate)
        {
            Process? process;
            lock (_gate) process = DetachLocked();
            Retire(process);
        }
    }

    private bool IsCurrent(Process process, long generation) =>
        !_disposed && generation == _generation && ReferenceEquals(_process, process);

    private Process? DetachLocked()
    {
        ++_generation;
        var process = _process;
        _process = null; _target = null;
        return process;
    }

    private void Retire(Process? process)
    {
        if (process is null) return;
        ProcessHelper.TryKill(process);
        process.Dispose();
        LogEmitted?.Invoke("[LinkBridge] 已停止共享");
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            Process? process;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true; process = DetachLocked();
            }
            Retire(process);
        }
    }
    private static string ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(char.IsWhiteSpace))
            throw new ArgumentException("推流目标地址无效。", nameof(host));
        if (System.Net.IPAddress.TryParse(host, out var address))
        {
            if (address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any))
                throw new ArgumentException("推流目标不能为未指定地址。", nameof(host));
            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        }
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || host.IndexOfAny(['/', '\\', '?', '#', ':', '@']) >= 0)
            throw new ArgumentException("推流目标地址无效。", nameof(host));
        return new System.Globalization.IdnMapping().GetAscii(host);
    }

    /// <summary>按平台拼装 ffmpeg 参数。</summary>
    private static List<string> BuildArguments(
        MediaDevice? camera,
        MediaDevice? microphone,
        string host,
        int port,
        AppSettings settings)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin" };
        var frameRate = settings.FrameRate.ToString();

        if (PlatformInfo.IsWindows)
        {
            if (camera is not null)
            {
                args.AddRange(["-f", "dshow", "-rtbufsize", "256M", "-i", $"video={camera.Id}"]);
            }

            if (microphone is not null)
            {
                args.AddRange(["-f", "dshow", "-rtbufsize", "128M", "-i", $"audio={microphone.Id}"]);
            }
        }
        else
        {
            // AVFoundation 用「视频索引:音频索引」的写法，没有的那一路写 none
            var video = camera?.Id ?? "none";
            var audio = microphone?.Id ?? "none";
            args.AddRange(["-f", "avfoundation"]);
            if (camera is not null)
            {
                args.AddRange(["-pixel_format", "uyvy422", "-framerate", frameRate]);
            }
            args.AddRange(["-i", $"{video}:{audio}"]);
        }

        if (camera is not null)
        {
            if (settings.UseHardwareEncoder)
            {
                args.AddRange(["-c:v", PlatformInfo.IsMacOS ? "h264_videotoolbox" : "h264_nvenc"]);
            }
            else
            {
                args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency"]);
            }

            args.AddRange([
                "-b:v", $"{settings.VideoBitrateMbps}M",
                "-pix_fmt", "yuv420p",
                "-g", frameRate,
            ]);
        }

        if (microphone is not null)
        {
            args.AddRange(["-c:a", "aac", "-b:a", $"{settings.AudioBitrateKbps}k", "-ar", "48000", "-ac", "2"]);
        }

        args.AddRange(["-f", "mpegts", $"udp://{host}:{port}?pkt_size=1316"]);
        return args;
    }
}

