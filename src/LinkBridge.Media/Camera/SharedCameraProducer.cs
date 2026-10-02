using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using LinkBridge.Core;
using LinkBridge.Core.Models;

namespace LinkBridge.Media.Camera;

/// <summary>Native Windows/macOS 4K camera producer; readiness requires matching input metadata and encoded frames.</summary>
public sealed class SharedCameraProducer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _retired = [];
    private Session? _session;
    private long _generation;
    private bool _disposed;
    private string? _lastError;
    private readonly Func<IReadOnlyList<string>, ProcessStartInfo>? _testCaptureFactory;
    public SharedCameraProducer() { }
    /// <summary>Internal isolated synthetic-process injection; production always uses the native backend.</summary>
    internal SharedCameraProducer(Func<IReadOnlyList<string>, ProcessStartInfo> captureFactory) => _testCaptureFactory = captureFactory;
    private static readonly Regex Dimensions = new(@"\b(?<w>\d{2,5})x(?<h>\d{2,5})\b", RegexOptions.CultureInvariant);
    private sealed class Session(Process process, CancellationTokenSource cancellation, long generation, string cameraId, string address, int port)
    {
        public readonly Process Process = process;
        public readonly CancellationTokenSource Cancellation = cancellation;
        public readonly long Generation = generation;
        public readonly string CameraId = cameraId, Address = address;
        public readonly int Port = port;
        public readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool InputConfirmed, HasFrames;
        public Task? Monitor;
    }
    public event Action<string>? LogEmitted;
    public event Action? StreamEnded;
    /// <summary>Unexpected EOF, delivered outside the producer lock with its exact source generation.</summary>
    public event Action<string, int, long>? SourceEnded;
    public long SourceGeneration { get { lock (_gate) return _generation; } }
    public int? TargetPort { get { lock (_gate) return _session?.Port; } }
    public string? TargetAddress { get { lock (_gate) return _session?.Address; } }
    public string? Target { get { lock (_gate) return _session is { } session ? $"{session.Address}:{session.Port}" : null; } }
    public string? LastError { get { lock (_gate) return _lastError; } }
    public bool IsRunning { get { lock (_gate) { try { return _session is { } session && session.Ready.Task.IsCompletedSuccessfully && session.Ready.Task.Result && !session.Process.HasExited; } catch { return false; } } } }

    public Task<bool> StartAsync(MediaDevice camera, string windowsIP, int port, CancellationToken ct = default)
    {
        var arguments = BuildArguments(camera, windowsIP, port);
        if (_testCaptureFactory is null && !((camera.Backend == MediaBackend.AvFoundation && OperatingSystem.IsMacOS())
            || (camera.Backend == MediaBackend.DirectShow && OperatingSystem.IsWindows())))
            throw new PlatformNotSupportedException("摄像头后端与当前操作系统不匹配。");
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is { } current && current.CameraId == camera.Id && current.Address == windowsIP && current.Port == port &&
                !current.Cancellation.IsCancellationRequested && !current.Process.HasExited)
                return current.Ready.Task.WaitAsync(ct);
            if (_session is { } previous) Log($"[LinkBridge] 4K摄像头目标变更：{previous.Address}:{previous.Port} → {windowsIP}:{port}");
            StopLocked();
            _lastError = null;
            var info = _testCaptureFactory?.Invoke(arguments) ?? new ProcessStartInfo(
                FfmpegLocator.ExecutablePath ?? throw new InvalidOperationException("本机没有找到ffmpeg，无法提供共享摄像头。"))
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            if (_testCaptureFactory is null) foreach (var argument in arguments) info.ArgumentList.Add(argument);
            var process = new Process { StartInfo = info };
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var session = new Session(process, cancellation, ++_generation, camera.Id, windowsIP, port);
            try
            {
                if (!process.Start()) throw new InvalidOperationException("4K摄像头采集进程启动失败。");
                _session = session;
                var token = cancellation.Token;
                session.Monitor = Task.Run(() => MonitorAsync(session, token));
                Log("[LinkBridge] 正在等待摄像头授权、原生3840×2160格式与首帧。");
                return session.Ready.Task.WaitAsync(ct);
            }
            catch
            {
                _session = null; ProcessHelper.TryKill(process); process.Dispose(); cancellation.Dispose(); throw;
            }
        }
    }
    internal static IReadOnlyList<string> BuildArguments(MediaDevice camera, string windowsIP, int port)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (camera.Kind != MediaDeviceKind.Camera || camera.IsApplicationEndpoint)
            throw new ArgumentException("共享4K摄像头来源必须为真实摄像头，不能使用应用入口回环。", nameof(camera));
        var index = 0;
        if (camera.Backend == MediaBackend.AvFoundation)
        {
            if (!int.TryParse(camera.Id, out index) || index is < 0 or > 4095)
                throw new ArgumentException("Mac共享4K摄像头必须使用实际AVFoundation视频设备索引。", nameof(camera));
        }
        else if (camera.Backend != MediaBackend.DirectShow || string.IsNullOrWhiteSpace(camera.Id)
            || camera.Id.Length > 4096 || camera.Id.Any(char.IsControl))
            throw new ArgumentException("Windows共享4K摄像头必须使用实际DirectShow设备标识。", nameof(camera));
        if (!IPAddress.TryParse(windowsIP, out var address)) throw new ArgumentException("共享摄像头目标必须为有效IP地址。", nameof(windowsIP));
        if (port is < 1025 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        if (camera.Backend == MediaBackend.DirectShow)
            return ["-hide_banner", "-loglevel", "info", "-nostdin", "-nostats", "-progress", "pipe:1",
                "-f", "dshow", "-rtbufsize", "256M", "-framerate", "30", "-video_size", "3840x2160", "-i", $"video={camera.Id}",
                "-an", "-c:v", "h264_nvenc", "-b:v", "16M", "-g", "30", "-pix_fmt", "yuv420p",
                "-f", "mpegts", $"udp://{host}:{port}?pkt_size=1316"];
        return ["-hide_banner", "-loglevel", "info", "-nostdin", "-nostats", "-progress", "pipe:1",
            "-f", "avfoundation", "-pixel_format", "uyvy422", "-framerate", "30", "-video_size", "3840x2160", "-i", $"{index}:none",
            "-an", "-c:v", "h264_videotoolbox", "-b:v", "16M", "-g", "30", "-pix_fmt", "yuv420p",
            "-f", "mpegts", $"udp://{host}:{port}?pkt_size=1316"];
    }
    internal static bool TryParseFrameProgress(string line, out long frame)
    {
        frame = 0;
        return line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6).Trim(), out frame) && frame > 0;
    }
    internal static bool TryParseInputDimensions(string line, out int width, out int height)
    {
        width = height = 0;
        if (!line.Contains("Video:", StringComparison.Ordinal)) return false;
        var dimensions = Dimensions.Match(line);
        return dimensions.Success && int.TryParse(dimensions.Groups["w"].Value, out width) && int.TryParse(dimensions.Groups["h"].Value, out height);
    }
    private bool IsCurrent(Session session) => !_disposed && ReferenceEquals(_session, session) && session.Generation == _generation && !session.Cancellation.IsCancellationRequested;
    private void CheckReadyLocked(Session session)
    {
        if (IsCurrent(session) && session.InputConfirmed && session.HasFrames && session.Ready.TrySetResult(true))
            Log($"[LinkBridge] 原生4K摄像头首帧已确认，30fps硬件编码发送 → {session.Address}:{session.Port}");
    }
    private async Task ReadProgressAsync(Session session, CancellationToken ct)
    {
        try
        {
            while (await session.Process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                if (TryParseFrameProgress(line, out _)) lock (_gate) { if (!IsCurrent(session)) return; session.HasFrames = true; CheckReadyLocked(session); }
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
    }
    private async Task ReadMetadataAsync(Session session, CancellationToken ct)
    {
        var inputSection = false;
        try
        {
            while (await session.Process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                lock (_gate)
                {
                    if (!IsCurrent(session)) return;
                    Log("[4K] " + line);
                    if (line.StartsWith("Input #0", StringComparison.Ordinal)) inputSection = true;
                    else if (line.StartsWith("Output #", StringComparison.Ordinal)) inputSection = false;
                    if (inputSection && TryParseInputDimensions(line, out var width, out var height))
                    {
                        if (width != 3840 || height != 2160)
                        {
                            _lastError = $"摄像头实际协商格式为{width}×{height}，未达到原生4K；已停止采集。";
                            Log("[错误] " + _lastError); session.Ready.TrySetResult(false);
                            session.Cancellation.Cancel(); ProcessHelper.TryKill(session.Process); return;
                        }
                        session.InputConfirmed = true; CheckReadyLocked(session);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
    }
    private async Task MonitorAsync(Session session, CancellationToken ct)
    {
        var progress = ReadProgressAsync(session, ct);
        var metadata = ReadMetadataAsync(session, ct);
        var exit = session.Process.WaitForExitAsync(ct);
        try
        {
            var deadline = Task.Delay(TimeSpan.FromSeconds(8), ct);
            var completed = await Task.WhenAny(session.Ready.Task, exit, deadline).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (completed != session.Ready.Task || !await session.Ready.Task.ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (IsCurrent(session))
                    {
                        _lastError ??= completed == deadline ? "8秒内未收到真实摄像头4K首帧，请检查本机摄像头授权或设备占用。" : "摄像头采集在首帧前退出，请检查本机摄像头授权、原生4K格式与硬件编码日志。";
                        Log("[错误] " + _lastError); session.Ready.TrySetResult(false);
                    }
                }
                return;
            }
            await exit.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (_gate) if (IsCurrent(session)) { _lastError = ex.Message; Log("[错误] 4K采集结束：" + ex.Message); } }
        finally
        {
            var unexpectedEnd = false;
            lock (_gate)
            {
                ProcessHelper.TryKill(session.Process);
                session.Ready.TrySetResult(false);
                if (ReferenceEquals(_session, session))
                {
                    unexpectedEnd = IsCurrent(session);
                    _session = null;
                }
            }
            // Discovery starts/stops producers while holding its own state lock.
            // Never call back into discovery while holding the producer lock.
            if (unexpectedEnd)
            {
                try { SourceEnded?.Invoke(session.Address, session.Port, session.Generation); } catch { }
                try { StreamEnded?.Invoke(); } catch { }
            }
            try { await Task.WhenAll(progress, metadata).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            try { await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            session.Process.Dispose(); session.Cancellation.Dispose();
        }
    }
    private void StopLocked()
    {
        ++_generation;
        var session = _session; _session = null;
        if (session is null) return;
        session.Cancellation.Cancel(); session.Ready.TrySetResult(false); ProcessHelper.TryKill(session.Process);
        if (session.Monitor is { } task) { _retired.Add(task); _ = task.ContinueWith(_ => { lock (_gate) _retired.Remove(task); }, TaskScheduler.Default); }
    }
    public void ProducerStopSync() { lock (_gate) StopLocked(); }
    public ValueTask StopAsync()
    {
        Task[] tasks;
        lock (_gate) { StopLocked(); tasks = _retired.ToArray(); }
        return new ValueTask(WaitRetiredAsync(tasks));
    }
    private static async Task WaitRetiredAsync(Task[] tasks)
    {
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
    }
    private void Log(string message) { try { LogEmitted?.Invoke(message); } catch { } }
    public ValueTask DisposeAsync() { lock (_gate) _disposed = true; return StopAsync(); }
}

