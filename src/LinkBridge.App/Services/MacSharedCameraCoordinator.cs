using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LinkBridge.Core;
using LinkBridge.Core.Discovery;

[assembly: InternalsVisibleTo("MacCameraCoordinatorProbe")]
namespace LinkBridge.App.Services;

public enum MacSharedCameraState { Unavailable, Ready, WaitingForPeer, Requesting, WaitingForFrames, Streaming, Failed, Stopped }

/// <summary>Owns only the legally installed Mac camera helper; CAMERA2 owns paired network authorization.</summary>
public sealed class MacSharedCameraCoordinator : IAsyncDisposable
{
    private readonly IMacCameraControl _control;
    private readonly IMacCameraHostFactory _hosts;
    private readonly MacCameraTiming _timing;
    private readonly int _port;
    private readonly CancellationTokenSource _life;
    private readonly Channel<MacCameraEvent> _events = Channel.CreateBounded<MacCameraEvent>(new BoundedChannelOptions(64)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly object _gate = new();
    private readonly List<Task> _stopJobs = [];
    private readonly HashSet<Session> _owned = [];
    private Task? _loop;
    private Session? _session;
    private string? _preferred;
    private long _nextProbe, _serial;
    private bool _startBridgeNext;
    private int _disposed, _state, _consumers;
    private string _status = "正在检查 Mac 原生共享摄像头入口";
    private sealed class Session(long id, IMacCameraHost host, string? peer, bool observe)
    {
        public readonly long Id = id;
        public readonly IMacCameraHost Host = host;
        public readonly string? Peer = peer;
        public readonly bool Observe = observe;
        public readonly CancellationTokenSource Cancel = new();
        public readonly HashSet<Task> Jobs = [];
        public readonly HashSet<string> RetiredGenerations = [];
        public string? Generation;
        public volatile string? Failure;
        public int Consumers;
        public bool Paired, Fresh, DemandSeen;
        public long LastHeartbeat = Environment.TickCount64, NextRenew, Request;
    }

    public MacSharedCameraCoordinator(PeerDiscovery discovery, int receivePort = 45683, CancellationToken lifetime = default)
        : this(new DiscoveryCameraControl(discovery), new InstalledMacCameraHostFactory(), receivePort, lifetime, MacCameraTiming.Production) { }

    internal MacSharedCameraCoordinator(IMacCameraControl control, IMacCameraHostFactory hosts, int port,
        CancellationToken lifetime, MacCameraTiming timing)
    {
        if (port is < 1025 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _control = control; _hosts = hosts; _port = port; _timing = timing;
        _life = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }
    public event Action<string>? StatusChanged;
    public MacSharedCameraState State => (MacSharedCameraState)Volatile.Read(ref _state);
    public int ConsumerCount => Volatile.Read(ref _consumers);
    public string Status => Volatile.Read(ref _status);
    public void SetPreferredWindowsAddress(string? address)
    {
        if (address is not null && (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            throw new ArgumentException("Windows camera peer must be IPv4.", nameof(address));
        Volatile.Write(ref _preferred, address);
        var current = Volatile.Read(ref _session);
        if (current is not null && !current.Observe && current.Peer != _control.FindWindowsAddress(address))
        {
            current.Failure = "Windows 摄像头来源已改变"; current.Cancel.Cancel(); current.Host.StopSync();
            if (current.Peer is { } peer) StopRemote(peer);
        }
    }
    public void Start()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed != 0, this); _loop ??= Task.Run(RunAsync); }
    }
    private void StatusIs(MacSharedCameraState state, string text)
    {
        if (Volatile.Read(ref _status) == text && State == state) return;
        Volatile.Write(ref _state, (int)state); Volatile.Write(ref _status, text);
        try { StatusChanged?.Invoke(text); } catch { }
    }
    private bool Current(Session session, string? generation = null) => Volatile.Read(ref _disposed) == 0 &&
        ReferenceEquals(Volatile.Read(ref _session), session) && !session.Cancel.IsCancellationRequested &&
        (generation is null || session.Generation == generation);

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(_timing.Pulse);
        try
        {
            while (!_life.IsCancellationRequested)
            {
                while (_events.Reader.TryRead(out var item)) await ApplyAsync(item).ConfigureAwait(false);
                var now = Environment.TickCount64;
                var session = Volatile.Read(ref _session);
                var peer = _control.FindWindowsAddress(Volatile.Read(ref _preferred));
                if (session is not null && ((!session.Observe && session.Peer != peer) || session.Failure is not null ||
                    session.Cancel.IsCancellationRequested || now - session.LastHeartbeat > _timing.Heartbeat.TotalMilliseconds ||
                    (session.Observe && session.Consumers > 0 && peer is not null) || (!session.Observe && session.DemandSeen && session.Consumers == 0 && session.Generation is null)))
                {
                    var switching = session.Failure == "Windows 摄像头来源已改变" || (session.Observe && session.Consumers > 0 && peer is not null) ||
                        (!session.Observe && session.DemandSeen && session.Consumers == 0 && session.Generation is null);
                    var failure = session.Failure ?? (switching ? "摄像头需求已变化" : "摄像头辅助进程心跳超时");
                    await EndAsync(session).ConfigureAwait(false);
                    StatusIs(MacSharedCameraState.Failed, failure); _nextProbe = now + (long)_timing.Probe.TotalMilliseconds;
                    session = null;
                    if (switching) _nextProbe = 0;
                }
                if (session is null && now >= _nextProbe)
                {
                    _nextProbe = now + (long)_timing.Probe.TotalMilliseconds;
                    MacCameraAvailability available;
                    try { available = await _hosts.ProbeAsync(_life.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (!_life.IsCancellationRequested) { available = new(false, "Mac 摄像头入口检查失败：" + ex.Message); }
                    if (!available.Available) StatusIs(MacSharedCameraState.Unavailable, available.Reason);
                    else
                    {
                        var observe = peer is null || !(_session is { Consumers: > 0 });
                        // Demand persists in the extension even across helper replacement.
                        // A watcher reporting demand is replaced by a peer-bound bridge.
                        if (peer is not null && _startBridgeNext) { observe = false; _startBridgeNext = false; }
                        IMacCameraHost host;
                        try { host = observe ? await _hosts.StartObserverAsync(_life.Token).ConfigureAwait(false)
                            : await _hosts.StartBridgeAsync(peer!, _port, _life.Token).ConfigureAwait(false); }
                        catch (Exception ex) when (!_life.IsCancellationRequested)
                        { StatusIs(MacSharedCameraState.Failed, "摄像头辅助进程启动失败：" + ex.Message); continue; }
                        session = new(++_serial, host, observe ? null : peer, observe);
                        var abandoned = false;
                        lock (_gate)
                        {
                            abandoned = _disposed != 0 || _life.IsCancellationRequested;
                            if (!abandoned) { _owned.Add(session); Volatile.Write(ref _session, session); }
                        }
                        if (abandoned) { host.StopSync(); await host.DisposeAsync().ConfigureAwait(false); session.Cancel.Dispose(); throw new OperationCanceledException(_life.Token); }
                        _ = ReadHostAsync(session);
                        StatusIs(MacSharedCameraState.Ready, "Mac 原生共享摄像头就绪 · 等待应用调用");
                    }
                }
                if (session is not null && !session.Observe && session.Peer is not null && Current(session) && session.Consumers > 0 &&
                    session.Generation is { } generation && now >= session.NextRenew)
                {
                    session.NextRenew = now + (long)_timing.Renew.TotalMilliseconds;
                    if (session.Jobs.Count < 3)
                    {
                        var job = PrepareAsync(session, generation);
                        session.Jobs.Add(job);
                    }
                }
                if (session is not null) session.Jobs.RemoveWhere(t => t.IsCompleted);
                lock (_gate) _stopJobs.RemoveAll(t => t.IsCompleted);
                if (!await timer.WaitForNextTickAsync(_life.Token).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception ex) { StatusIs(MacSharedCameraState.Failed, "Mac 摄像头服务失败：" + ex.Message); }
        finally
        {
            if (Volatile.Read(ref _session) is { } session) await EndAsync(session).ConfigureAwait(false);
            StatusIs(MacSharedCameraState.Stopped, "Mac 共享摄像头服务已停止");
        }
    }
    private async Task ReadHostAsync(Session session)
    {
        try
        {
            while (!session.Cancel.IsCancellationRequested)
            {
                var line = await session.Host.ReadLineAsync(session.Cancel.Token).ConfigureAwait(false);
                if (line is null) { session.Failure = "摄像头辅助进程已退出"; break; }
                if (Encoding.UTF8.GetByteCount(line) > 4096) throw new InvalidDataException("摄像头辅助进程消息超过限额");
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid camera helper JSON.");
                var kind = root.GetProperty("event").GetString();
                if (kind == "error") { session.Failure = "Mac 摄像头后端报告错误"; break; }
                if (kind is not ("start" or "stop" or "heartbeat")) throw new InvalidDataException("Unknown camera helper event.");
                if (root.GetProperty("v").GetInt32() != 1) throw new InvalidDataException("Unknown helper version.");
                var generation = root.GetProperty("generation").GetString() ?? "";
                var count = root.GetProperty("consumers").GetInt32();
                if (count is < 0 or > 256 || (count > 0 && !Guid.TryParse(generation, out _))) throw new InvalidDataException("Invalid camera demand.");
                if (kind == "start" && (root.GetProperty("width").GetInt32() != 3840 || root.GetProperty("height").GetInt32() != 2160 ||
                    root.GetProperty("fps").GetInt32() != 30 || root.GetProperty("port").GetInt32() != (session.Observe ? 0 : _port)))
                    throw new InvalidDataException("Helper requested a non-native camera format.");
                var fresh = root.TryGetProperty("freshFrames", out var freshValue) && freshValue.ValueKind == JsonValueKind.True;
                if (!_events.Writer.TryWrite(new(session.Id, kind!, generation, count, fresh, null)))
                    throw new InvalidDataException("Camera helper event backlog exceeded.");
            }
        }
        catch (OperationCanceledException) when (session.Cancel.IsCancellationRequested) { }
        catch (Exception ex) { session.Failure = "摄像头辅助进程消息无效：" + ex.Message; }
        finally { if (session.Failure is not null) session.Host.StopSync(); }
    }
    private async Task ApplyAsync(MacCameraEvent item)
    {
        var session = Volatile.Read(ref _session);
        if (session is null || session.Id != item.Session || !Current(session)) return;
        if (item.Kind == "ack")
        {
            if (session.Generation != item.Generation || session.Request != item.Request || session.Consumers == 0 || session.Peer != _control.FindWindowsAddress(Volatile.Read(ref _preferred))) return;
            if (item.Ready == true)
            {
                if (!await WriteAsync(session, "pairedReady", item.Generation).ConfigureAwait(false)) return;
                if (!Current(session, item.Generation) || session.Consumers == 0 || session.Peer != _control.FindWindowsAddress(Volatile.Read(ref _preferred))) return;
                session.Paired = true;
                StatusIs(session.Fresh ? MacSharedCameraState.Streaming : MacSharedCameraState.WaitingForFrames,
                    session.Fresh ? "Mac 原生 4K 已送入系统摄像头" : "Windows 已确认原生 4K · 等待 Mac 系统帧");
            }
            else
            {
                session.Paired = session.Fresh = false;
                await WriteAsync(session, "pairedStopped", item.Generation).ConfigureAwait(false);
                if (session.Peer is { } peer) StopRemote(peer);
                session.Failure = "Windows 摄像头未确认原生 4K · 等待授权或设备恢复";
                session.Host.StopSync(); StatusIs(MacSharedCameraState.Failed, session.Failure);
            }
            return;
        }
        // A retired demand epoch cannot stop or renew a newer one on the same helper.
        if (session.RetiredGenerations.Contains(item.Generation) ||
            (item.Kind != "start" && session.Generation is { } active && active != item.Generation)) return;
        session.LastHeartbeat = Environment.TickCount64;
        session.DemandSeen = true; // Initial native zero also covers demand disappearing during watcher replacement.
        if (item.Count == 0)
        {
            if (session.Consumers > 0)
            {
                session.Consumers = 0; Volatile.Write(ref _consumers, 0); session.Paired = session.Fresh = false;
                var previous = session.Generation; session.Generation = null;
                if (previous is not null) session.RetiredGenerations.Add(previous);
                if (previous is not null) await WriteAsync(session, "pairedStopped", previous).ConfigureAwait(false);
                if (session.Peer is { } peer) StopRemote(peer);
            }
            StatusIs(MacSharedCameraState.Ready, "Mac 原生共享摄像头就绪 · 等待应用调用"); return;
        }
        if (session.Generation != item.Generation)
        {
            var previous = session.Generation;
            if (previous is not null) { session.RetiredGenerations.Add(previous); await WriteAsync(session, "pairedStopped", previous).ConfigureAwait(false); if (session.Peer is { } peer) StopRemote(peer); }
            if (session.RetiredGenerations.Count > 64) { session.Failure = "摄像头需求代次超过单进程限额 · 正在重新建立入口"; session.Host.StopSync(); return; }
            session.Generation = item.Generation; session.Paired = session.Fresh = false; session.NextRenew = 0;
        }
        session.Consumers = item.Count; session.Fresh = item.Fresh;
        if (session.Observe && item.Count > 0) _startBridgeNext = true;
        Volatile.Write(ref _consumers, item.Count);
        StatusIs(session.Paired && session.Fresh ? MacSharedCameraState.Streaming : session.Paired ? MacSharedCameraState.WaitingForFrames : MacSharedCameraState.Requesting,
            session.Paired && session.Fresh ? "Mac 原生 4K 已送入系统摄像头" : session.Paired ? "Windows 已确认原生 4K · 等待 Mac 系统帧" : "应用已调用 · 正在请求 Windows 原生 4K");
    }
    private async Task PrepareAsync(Session session, string generation)
    {
        var request = ++session.Request;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, session.Cancel.Token);
        timeout.CancelAfter(_timing.Request);
        try
        {
            var ready = await _control.PrepareAsync(session.Peer!, _port, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (Current(session, generation) && session.Consumers > 0)
                _events.Writer.TryWrite(new(session.Id, "ack", generation, session.Consumers, false, ready, request));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (Current(session, generation)) _events.Writer.TryWrite(new(session.Id, "ack", generation, session.Consumers, false, false, request)); }
    }
    private async Task<bool> WriteAsync(Session session, string kind, string generation)
    {
        using var timeout = new CancellationTokenSource(_timing.Write);
        try
        {
            var line = JsonSerializer.Serialize(new { v = 1, @event = kind, generation });
            await session.Host.WriteLineAsync(line, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception) { session.Failure = "摄像头辅助进程控制写入超时或失败"; session.Host.StopSync(); return false; }
    }
    private void StopRemote(string peer)
    {
        var task = StopRemoteAsync(peer);
        lock (_gate) _stopJobs.Add(task);
    }
    private async Task StopRemoteAsync(string peer)
    {
        using var timeout = new CancellationTokenSource(_timing.Write);
        try { await _control.StopAsync(peer, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); } catch { }
    }
    private async Task EndAsync(Session session)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session)) return;
        session.Cancel.Cancel(); Volatile.Write(ref _consumers, 0);
        if (session.Generation is { } generation) await WriteAsync(session, "pairedStopped", generation).ConfigureAwait(false);
        if (session.Peer is { } peer) StopRemote(peer);
        session.Host.StopSync();
        await session.Host.DisposeAsync().ConfigureAwait(false);
        lock (_gate) _owned.Remove(session);
        var jobs = Task.WhenAll(session.Jobs);
        try { await jobs.WaitAsync(_timing.Write).ConfigureAwait(false); } catch { }
        if (jobs.IsCompleted) session.Cancel.Dispose(); else _ = jobs.ContinueWith(_ => session.Cancel.Dispose(), TaskScheduler.Default);
    }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _life.Cancel();
        Session[] owned; lock (_gate) owned = _owned.ToArray();
        foreach (var session in owned)
        {
            session.Cancel.Cancel(); if (session.Peer is { } peer) StopRemote(peer); session.Host.StopSync();
        }
        return new ValueTask(FinishDisposeAsync());
    }
    private async Task FinishDisposeAsync()
    {
        Task? loop; lock (_gate) loop = _loop;
        try { if (loop is not null) await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        Task[] stops; lock (_gate) stops = _stopJobs.ToArray();
        try { await Task.WhenAll(stops).WaitAsync(_timing.Write).ConfigureAwait(false); } catch { }
        if (loop is null || loop.IsCompleted) _life.Dispose(); else _ = loop.ContinueWith(_ => _life.Dispose(), TaskScheduler.Default);
    }
}

internal sealed record MacCameraEvent(long Session, string Kind, string Generation, int Count, bool Fresh, bool? Ready, long Request = 0);
internal sealed record MacCameraAvailability(bool Available, string Reason);
internal sealed record MacCameraTiming(TimeSpan Pulse, TimeSpan Renew, TimeSpan Request, TimeSpan Write, TimeSpan Probe, TimeSpan Heartbeat)
{
    public static MacCameraTiming Production { get; } = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(6));
}
internal interface IMacCameraControl
{
    string? FindWindowsAddress(string? preferred);
    Task<bool> PrepareAsync(string address, int port, CancellationToken cancellation);
    Task StopAsync(string address, CancellationToken cancellation);
}
internal interface IMacCameraHostFactory
{
    Task<MacCameraAvailability> ProbeAsync(CancellationToken cancellation);
    Task<IMacCameraHost> StartObserverAsync(CancellationToken cancellation);
    Task<IMacCameraHost> StartBridgeAsync(string peer, int port, CancellationToken cancellation);
}
internal interface IMacCameraHost : IAsyncDisposable
{
    Task<string?> ReadLineAsync(CancellationToken cancellation);
    Task WriteLineAsync(string line, CancellationToken cancellation);
    void StopSync();
}
internal sealed class DiscoveryCameraControl(PeerDiscovery discovery) : IMacCameraControl
{
    public string? FindWindowsAddress(string? preferred)
    {
        if (discovery.PairingSecret.Length != 32) return null;
        var peers = discovery.Snapshot().Where(p => p.Platform.Contains("Windows", StringComparison.OrdinalIgnoreCase));
        return (preferred is null ? peers.FirstOrDefault() : peers.FirstOrDefault(p => p.Address == preferred))?.Address;
    }
    public Task<bool> PrepareAsync(string address, int port, CancellationToken cancellation) => discovery.PrepareRemoteCameraAsync(address, port, cancellation);
    public async Task StopAsync(string address, CancellationToken cancellation) => _ = await discovery.StopRemoteCameraAsync(address, cancellation).ConfigureAwait(false);
}

internal sealed class InstalledMacCameraHostFactory : IMacCameraHostFactory
{
    private const string Bundle = "/Applications/LinkBridgeCameraHost.app";
    private const string Executable = Bundle + "/Contents/MacOS/LinkBridgeCameraHost";
    public async Task<MacCameraAvailability> ProbeAsync(CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(Executable)) return new(false, "Mac 原生摄像头扩展尚未安装 · 需要合法 Apple 签名与系统批准");
        if ((File.GetAttributes(Executable) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(Bundle) & FileAttributes.ReparsePoint) != 0)
            return new(false, "摄像头 host 必须部署在固定的真实 /Applications 应用包");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var verified = await CommandAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", Bundle], timeout.Token).ConfigureAwait(false);
        if (verified.Exit != 0) return new(false, "Mac 摄像头 host 完整签名验证失败");
        var check = await CommandAsync(Executable, ["--check-activation"], timeout.Token).ConfigureAwait(false);
        if (check.Exit != 0) return new(false, "Mac 摄像头 host 缺合法 Apple Team 或部署条件");
        using var eligibility = JsonDocument.Parse(check.Line);
        if (!eligibility.RootElement.GetProperty("eligible").GetBoolean() || !eligibility.RootElement.GetProperty("hasAppleTeam").GetBoolean())
            return new(false, "Mac 摄像头 host 缺合法 Apple Team");
        var status = await CommandAsync(Executable, ["--status"], timeout.Token).ConfigureAwait(false);
        using var found = JsonDocument.Parse(status.Line);
        if (status.Exit != 0 || !found.RootElement.GetProperty("found").GetBoolean() || found.RootElement.GetProperty("width").GetInt32() != 3840 ||
            found.RootElement.GetProperty("height").GetInt32() != 2160 || found.RootElement.GetProperty("fps").GetInt32() != 30)
            return new(false, "Mac 原生摄像头扩展未启用 · 需用户批准系统扩展");
        if (FfmpegLocator.ExecutablePath is not { } ffmpeg || !Path.IsPathFullyQualified(ffmpeg)) return new(false, "缺少可执行的绝对 FFmpeg 路径");
        return new(true, "Mac 原生共享摄像头入口已验证");
    }
    public Task<IMacCameraHost> StartBridgeAsync(string peer, int port, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var ffmpeg = FfmpegLocator.ExecutablePath ?? throw new InvalidOperationException("FFmpeg missing.");
        return Task.FromResult<IMacCameraHost>(new MacCameraProcessHost(Executable, ["--bridge", peer, port.ToString(), ffmpeg]));
    }
    public Task<IMacCameraHost> StartObserverAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult<IMacCameraHost>(new MacCameraProcessHost(Executable, ["--observe"]));
    }
    private static async Task<(int Exit, string Line)> CommandAsync(string executable, string[] arguments, CancellationToken cancellation)
    {
        var info = MacCameraProcessHost.Info(executable, arguments);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Camera preflight process did not start.");
        try
        {
            var stdout = MacCameraProcessHost.ReadBoundedAsync(process.StandardOutput, cancellation);
            var stderr = MacCameraProcessHost.DrainBoundedAsync(process.StandardError, cancellation);
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            var line = await stdout.ConfigureAwait(false); await stderr.ConfigureAwait(false);
            return (process.ExitCode, line ?? "{}");
        }
        finally { ProcessHelper.TryKill(process); }
    }
}
internal sealed class MacCameraProcessHost : IMacCameraHost
{
    private readonly Process _process;
    private readonly SemaphoreSlim _write = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _stderr;
    private int _closed;
    internal static ProcessStartInfo Info(string executable, string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    public MacCameraProcessHost(string executable, string[] arguments)
    {
        _process = new Process { StartInfo = Info(executable, arguments) };
        if (!_process.Start()) throw new IOException("Camera helper did not start.");
        _stderr = DrainBoundedAsync(_process.StandardError, _stop.Token);
    }
    public Task<string?> ReadLineAsync(CancellationToken cancellation) => ReadBoundedAsync(_process.StandardOutput, cancellation);
    public async Task WriteLineAsync(string line, CancellationToken cancellation)
    {
        if (line.Length > 4096) throw new InvalidDataException("Helper command oversized.");
        await _write.WaitAsync(cancellation).ConfigureAwait(false);
        try { await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellation).ConfigureAwait(false); await _process.StandardInput.FlushAsync(cancellation).ConfigureAwait(false); }
        finally { _write.Release(); }
    }
    internal static async Task<string?> ReadBoundedAsync(StreamReader reader, CancellationToken cancellation)
    {
        var builder = new StringBuilder(); var character = new char[1]; var bytes = 0;
        while (await reader.ReadAsync(character.AsMemory(), cancellation).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return builder.ToString().TrimEnd('\r');
            bytes += character[0] <= 0x7f ? 1 : character[0] <= 0x7ff ? 2 : 3;
            if (bytes > 4096) throw new InvalidDataException("Helper line oversized.");
            builder.Append(character[0]);
        }
        return builder.Length == 0 ? null : builder.ToString();
    }
    internal static async Task DrainBoundedAsync(StreamReader reader, CancellationToken cancellation)
    {
        var buffer = new char[512]; while (await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false) != 0) { }
    }
    public void StopSync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        try { _process.StandardInput.Close(); } catch { }
        ProcessHelper.TryKill(_process);
    }
    public async ValueTask DisposeAsync()
    {
        StopSync();
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        try { await _stderr.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        _process.Dispose();
        if (_stderr.IsCompleted) _stop.Dispose(); else _ = _stderr.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
