using System.Net;
using System.Net.NetworkInformation;
using LinkBridge.Core;
using LinkBridge.Core.Discovery;
using LinkBridge.Core.Models;
using LinkBridge.Media;
using LinkBridge.Media.Audio;

namespace LinkBridge.App.Services;

/// <summary>
/// Joins signed MIC demand control, LBM1 TLS, physical-source consent and a dedicated
/// virtual sink. Source and consumer actors are independently serialized: two machines
/// can request each other's microphone without deadlocking on each other's MIC_READY.
/// </summary>
public sealed class SharedMicrophoneCoordinator : IAsyncDisposable
{
    private readonly IMicrophoneControl _control;
    private readonly IMicrophoneCoordinatorFactory _factory;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sourceGate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _sourceTasks = [], _stopTasks = [], _prepareTasks = [];
    private readonly HashSet<string> _requestedAddresses = [];
    private readonly HashSet<string> _localAddresses;
    private Task? _worker, _disposeTask;
    private bool _started, _disposed, _knownDemand;
    private int? _consumers;
    private MediaDevice? _selected;
    private MicrophoneSourceRoute? _source;
    private MicrophoneReceiveRoute? _receive;
    private IMicrophoneReceiverHandle? _receiver;
    private IMicrophoneDemandHandle? _monitor;
    private SharedMicrophoneEndpoint? _endpoint;
    private long _sourceEpoch, _receiveEpoch, _prepareVersion;
    private int _sourceReportsPending;
    private DateTimeOffset _lastAttempt, _lastDiagnose;
    public event Action<string>? StatusChanged;

    public SharedMicrophoneCoordinator(PeerDiscovery discovery, string secret)
        : this(new DiscoveryMicrophoneControl(discovery), new NativeMicrophoneCoordinatorFactory(secret)) { }

    internal SharedMicrophoneCoordinator(IMicrophoneControl control, IMicrophoneCoordinatorFactory factory)
    {
        _control = control; _factory = factory;
        _localAddresses = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address.ToString()).ToHashSet(StringComparer.Ordinal);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            _control.StartRequested += OnSourceRequested;
            _control.StopRequested += OnSourceStopRequested;
            _worker = Task.Run(RunConsumerAsync);
        }
    }

    public void SelectLocalSource(MediaDevice? selectedPhysicalMic)
    {
        if (selectedPhysicalMic is not null && !IsPhysical(selectedPhysicalMic))
            throw new ArgumentException("麦克风共享来源必须为实体麦克风，不能是应用输入或虚拟回环。", nameof(selectedPhysicalMic));
        MicrophoneSourceRoute? old;
        lock (_gate)
        {
            if (_disposed) return;
            if (_selected == selectedPhysicalMic) return;
            _selected = selectedPhysicalMic;
            old = TakeSourceLocked();
            if (old is not null) _sourceReportsPending++;
        }
        if (old is not null)
        {
            StopSourceImmediately(old);
            ReportSourceEnded(old.Address, old.Port);
        }
        Status(selectedPhysicalMic is null ? "本机麦克风未选择，已停止向对端提供采集。" : "已选择实体麦克风；仅在已认证对端请求时采集。");
    }

    private Task<bool> OnSourceRequested(string address, int port)
    {
        if (port is < 1025 or > 65535 || !AllowedPeer(address)) return Task.FromResult(false);
        MicrophoneSourceRoute? old;
        MicrophoneSourceRoute route;
        lock (_gate)
        {
            if (!_started || _disposed || _selected is null || _sourceReportsPending != 0) return Task.FromResult(false);
            if (_source is { } current && current.Address == address && current.Port == port
                && current.Device == _selected && current.Sender.IsRunning && current.Sender.SentBytes > 0)
                return Task.FromResult(true);
            old = TakeSourceLocked();
            route = new(address, port, ++_sourceEpoch, _selected, _factory.CreateSender(port), _lifetime.Token);
            _source = route;
            route.Sender.StreamEnded += () => OnSourceEnded(route);
        }
        if (old is not null) StopSourceImmediately(old); // Core already owns the replacement signed epoch.
        var task = Task.Run(() => StartSourceAsync(route));
        Track(task, _sourceTasks);
        return task;
    }

    private async Task<bool> StartSourceAsync(MicrophoneSourceRoute route)
    {
        var acquired = false;
        var succeeded = false;
        try
        {
            await _sourceGate.WaitAsync(route.Token).ConfigureAwait(false); acquired = true;
            if (!CurrentSource(route)) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(route.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            // Native Sender opens physical capture only AFTER mutually authenticated sink permit.
            await route.Sender.StartAsync(route.Address, route.Device, deadline.Token).ConfigureAwait(false);
            var until = DateTimeOffset.UtcNow.AddSeconds(5);
            while (CurrentSource(route) && route.Sender.IsRunning && route.Sender.SentBytes == 0
                   && DateTimeOffset.UtcNow < until)
                await Task.Delay(25, deadline.Token).ConfigureAwait(false);
            succeeded = CurrentSource(route) && !deadline.IsCancellationRequested
                && route.Sender.IsRunning && route.Sender.SentBytes > 0;
            if (succeeded) Status("已按对端应用需求发送实体麦克风 PCM。" );
            return succeeded;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Status("共享麦克风来源启动失败：" + ex.Message); return false; }
        finally
        {
            route.StartCompleted.TrySetResult();
            if (!succeeded)
            {
                var report = false;
                lock (_gate)
                {
                    if (ReferenceEquals(_source, route))
                    { TakeSourceLocked(); _sourceReportsPending++; report = true; }
                }
                if (report)
                {
                    StopSourceImmediately(route);
                    ReportSourceEnded(route.Address, route.Port);
                }
                // Repeat Stop after an ignored/late factory result, even if revocation
                // already stopped this route while its asynchronous Start was pending.
                await CleanupSourceAsync(route).ConfigureAwait(false);
            }
            if (acquired) _sourceGate.Release();
        }
    }

    private void OnSourceEnded(MicrophoneSourceRoute route)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_source, route) || _disposed) return;
            TakeSourceLocked(); _sourceReportsPending++;
        }
        StopSourceImmediately(route);
        ReportSourceEnded(route.Address, route.Port);
        Status("实体麦克风采集已结束；已撤销对端缓存就绪状态。" );
    }

    private void OnSourceStopRequested(string address)
    {
        MicrophoneSourceRoute? route;
        lock (_gate) { route = _source?.Address == address ? TakeSourceLocked() : null; }
        if (route is not null) StopSourceImmediately(route);
    }
    private MicrophoneSourceRoute? TakeSourceLocked()
    {
        var old = _source; _source = null; _sourceEpoch++;
        if (old is not null) Cancel(old.Cancellation); return old;
    }
    private bool CurrentSource(MicrophoneSourceRoute route)
    {
        lock (_gate) return !_disposed && ReferenceEquals(_source, route) && route.Epoch == _sourceEpoch && !route.Token.IsCancellationRequested;
    }
    private void StopSourceImmediately(MicrophoneSourceRoute route)
    {
        Cancel(route.Cancellation);
        // StopAsync's synchronous prefix kills capture and closes TLS immediately.
        var stop = route.Sender.StopAsync();
        Track(FinishSourceStopAsync(route, stop), _stopTasks);
    }
    private async Task FinishSourceStopAsync(MicrophoneSourceRoute route, ValueTask stop)
    {
        try { await stop.AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) { Status("麦克风停止清理：" + ex.Message); }
        await CleanupSourceAsync(route).ConfigureAwait(false);
    }
    private async Task CleanupSourceAsync(MicrophoneSourceRoute route)
    {
        try { await route.Sender.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) { Status("麦克风停止等待：" + ex.Message); }
        try { await route.Sender.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) { Status("麦克风释放：" + ex.Message); }
        _ = route.StartCompleted.Task.ContinueWith(_ => route.Cancellation.Dispose(), TaskScheduler.Default);
    }

    private async Task RunConsumerAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    if (DateTimeOffset.UtcNow - _lastDiagnose >= TimeSpan.FromSeconds(5))
                    { _lastDiagnose = DateTimeOffset.UtcNow; await RefreshEndpointAsync().ConfigureAwait(false); }
                    await ReconcileConsumerAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { } // consumer revoked while signed START was pending
                catch (Exception ex) { Status("共享麦克风协调失败：" + ex.Message); RevokeConsumer(); }
                if (!await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task RefreshEndpointAsync()
    {
        var endpoint = _factory.Diagnose();
        lock (_gate) { if (_disposed) return; if (_endpoint == endpoint && _receiver is not null) return; }
        RevokeConsumer();
        IMicrophoneReceiverHandle? oldReceiver; IMicrophoneDemandHandle? oldMonitor;
        lock (_gate) { oldReceiver = _receiver; oldMonitor = _monitor; _receiver = null; _monitor = null; _endpoint = endpoint; _knownDemand = false; _consumers = null; }
        oldMonitor?.Dispose();
        if (oldReceiver is not null) await oldReceiver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        _control.HasInput = false;
        if (!endpoint.Available || endpoint.Id is null || endpoint.ApplicationInputId is null)
        { Status(endpoint.Diagnostic + " 本机仍可在选中实体麦克风后向已认证对端提供来源。" ); return; }
        var receiver = _factory.CreateReceiver(endpoint.Id, ExpectedPeer, PermitDemand);
        IMicrophoneDemandHandle? monitor = null;
        try
        {
            receiver.ConnectionChanged += connected =>
            {
                if (PermitDemand()) Status(connected ? "对端麦克风正在送入 " + endpoint.ApplicationInputName : "应用仍有麦克风需求，正在等待对端 PCM。" );
            };
            // Publish ownership before Start so concurrent disposal can stop this
            // exact receiver; a slow factory result after shutdown never starts it.
            lock (_gate)
            {
                if (_disposed) throw new OperationCanceledException();
                _receiver = receiver;
            }
            receiver.Start();
            monitor = _factory.CreateDemand(OnDemandChanged);
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_receiver, receiver)) throw new OperationCanceledException();
                _monitor = monitor; _control.HasInput = true;
            }
            monitor.Start(endpoint.ApplicationInputId);
            Status("共享麦克风应用输入已就绪，等待可确认的应用录制需求。" );
        }
        catch
        {
            monitor?.Dispose(); receiver.Disconnect();
            await receiver.DisposeAsync().ConfigureAwait(false);
            lock (_gate) { if (ReferenceEquals(_receiver, receiver)) { _receiver = null; _monitor = null; } }
            _control.HasInput = false; throw;
        }
    }

    private void OnDemandChanged(MicrophoneDemandSnapshot demand)
    {
        bool revoke;
        lock (_gate)
        {
            if (_disposed) return;
            _knownDemand = demand.Status == MicrophoneDemandStatus.Known && demand.ActiveConsumers is not null;
            _consumers = _knownDemand ? demand.ActiveConsumers : null;
            revoke = !_knownDemand || _consumers is not > 0;
        }
        if (revoke)
        {
            RevokeConsumer();
            Status(demand.Status == MicrophoneDemandStatus.Known ? "应用麦克风需求已结束，已停止接收与远端采集。" : "无法确认应用麦克风需求，已撤销采集许可：" + demand.Detail);
        }
    }
    private bool PermitDemand()
    { lock (_gate) return !_disposed && _knownDemand && _consumers is > 0 && _receive is not null; }
    private bool ExpectedPeer(string address)
    { lock (_gate) { if (_disposed || _receive?.Address != address) return false; } return AllowedPeer(address); }

    private async Task ReconcileConsumerAsync()
    {
        IMicrophoneReceiverHandle? receiver; bool requested; MicrophoneReceiveRoute? route;
        lock (_gate) { if (_disposed) return; receiver = _receiver; requested = _knownDemand && _consumers is > 0; route = _receive; }
        if (!requested || receiver is null) return;
        var peer = _control.Snapshot().Where(p => AllowedAddress(p.Address) && p.Microphones.Any(IsPhysical))
            .OrderBy(p => string.Equals(p.Platform, _control.LocalPlatform, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault();
        if (peer is null) { RevokeConsumer(); Status("应用需要麦克风，正在等待另一台电脑的实体麦克风。" ); return; }
        if (route?.Address != peer.Address)
        {
            RevokeConsumer();
            Task[] stops; lock (_gate) stops = _stopTasks.Where(t => !t.IsCompleted).ToArray();
            try { await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(2), _lifetime.Token).ConfigureAwait(false); }
            catch (TimeoutException) { return; }
            lock (_gate)
            {
                if (_disposed || !_knownDemand || _consumers is not > 0) return;
                route = new(peer.Address, ++_receiveEpoch, _lifetime.Token); _receive = route; _lastAttempt = default;
            }
        }
        if (DateTimeOffset.UtcNow - _lastAttempt < TimeSpan.FromSeconds(4)) return;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_receive, route) || !_knownDemand || _consumers is not > 0) return;
            // Keep actual unfinished calls bounded even if an injected/OS transport
            // ignores cancellation. Core cancels the previous same-address START.
            if (_prepareTasks.Count(t => !t.IsCompleted) >= 3) return;
            _lastAttempt = DateTimeOffset.UtcNow;
            _requestedAddresses.Add(route!.Address);
            var version = ++_prepareVersion;
            var job = Task.Run(() => PrepareConsumerAsync(route, receiver, version));
            _prepareTasks.Add(job);
            _ = job.ContinueWith(_ => { lock (_gate) _prepareTasks.Remove(job); }, TaskScheduler.Default);
        }
    }

    private async Task PrepareConsumerAsync(MicrophoneReceiveRoute route, IMicrophoneReceiverHandle receiver, long version)
    {
        try
        {
            // Do not await this job on the 1Hz reconciliation/endpoint-check actor.
            // Every 4s invocation creates a fresh signed nonce even if READY is lost.
            var ready = await _control.Prepare(route.Address, receiver.Port, route.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_receive, route) || route.Epoch != _receiveEpoch
                    || version != _prepareVersion || route.Token.IsCancellationRequested) return;
            }
            if (!ready && !receiver.IsConnected) Status("对端麦克风未确认就绪，请检查对端实体麦克风选择与权限。" );
            else if (ready && !receiver.IsConnected) Status("麦克风请求已签名确认，等待首个有效 PCM。" );
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (_gate) { if (_disposed || !ReferenceEquals(_receive, route) || version != _prepareVersion) return; }
            Status("麦克风签名控制请求失败：" + ex.Message);
        }
    }

    private void RevokeConsumer()
    {
        MicrophoneReceiveRoute? route; IMicrophoneReceiverHandle? receiver;
        lock (_gate)
        {
            route = _receive; if (route is not null) Cancel(route.Cancellation); _receive = null; _receiveEpoch++;
            receiver = _receiver;
        }
        receiver?.Disconnect();
        if (route is not null) Track(StopReceiveRouteAsync(route), _stopTasks);
    }
    private async Task StopReceiveRouteAsync(MicrophoneReceiveRoute route)
    {
        try { await RemoteStopAsync(route.Address).ConfigureAwait(false); }
        finally { route.Cancellation.Dispose(); }
    }
    private async Task RemoteStopAsync(string address)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            // Intentionally independent of the canceled receive/lifetime token.
            var stopped = await _control.Stop(address, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (stopped) lock (_gate) _requestedAddresses.Remove(address);
            else Status("远端麦克风停止未确认；本地接收已关闭，远端租约会失效。" );
        }
        catch (Exception ex) { Status("远端麦克风停止未确认：" + ex.Message); }
    }

    public ValueTask DisposeAsync()
    {
        MicrophoneSourceRoute? source; MicrophoneReceiveRoute? receive;
        IMicrophoneReceiverHandle? receiver; IMicrophoneDemandHandle? monitor; string[] addresses;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true; _lifetime.Cancel();
            source = TakeSourceLocked(); receive = _receive;
            if (receive is not null) Cancel(receive.Cancellation); _receive = null; _receiveEpoch++;
            _knownDemand = false; _consumers = null; receiver = _receiver; monitor = _monitor; addresses = _requestedAddresses.ToArray();
            _control.StartRequested -= OnSourceRequested; _control.StopRequested -= OnSourceStopRequested; _control.HasInput = false;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposeTask = completion.Task;
        }
        // Never call Media while holding _gate: receiver callbacks hold their own
        // lock and consult PermitDemand, so reversed lock order could deadlock.
        var failures = new List<Exception>();
        try { receiver?.Disconnect(); } catch (Exception ex) { failures.Add(ex); }
        ValueTask sourceStop = ValueTask.CompletedTask;
        try { sourceStop = source?.Sender.StopAsync() ?? ValueTask.CompletedTask; } catch (Exception ex) { failures.Add(ex); }
        receive?.Cancellation.Dispose();
        _ = FinishDisposeAsync(completion, source, sourceStop, receiver, monitor, addresses, failures);
        return new(_disposeTask);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource completion, MicrophoneSourceRoute? source, ValueTask sourceStop,
        IMicrophoneReceiverHandle? receiver, IMicrophoneDemandHandle? monitor, string[] addresses, List<Exception> failures)
    {
        try
        {
            // Remote STOP does not depend on monitor cleanup succeeding.
            var remoteStops = addresses.Select(RemoteStopAsync).ToArray();
            try { monitor?.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            Task receiverStop;
            try { receiverStop = receiver?.DisposeAsync().AsTask() ?? Task.CompletedTask; }
            catch (Exception ex) { failures.Add(ex); receiverStop = Task.CompletedTask; }
            var ownSourceStop = source is null ? Task.CompletedTask : FinishSourceStopAsync(source, sourceStop);
            Task[] starts; lock (_gate) starts = _sourceTasks.Concat(_prepareTasks).ToArray();
            var all = Task.WhenAll(remoteStops.Concat(starts).Concat(new[] { _worker ?? Task.CompletedTask, receiverStop, ownSourceStop }));
            try { await all.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            _ = all.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
            if (failures.Count == 0) completion.TrySetResult();
            else completion.TrySetException(new AggregateException("麦克风协调器关闭时发生错误，已独立尝试所有停止路径。", failures));
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
    private bool AllowedPeer(string address) => AllowedAddress(address) && _control.Snapshot().Any(p => p.Address == address);
    private bool AllowedAddress(string address) => IPAddress.TryParse(address, out var ip) && !IPAddress.IsLoopback(ip)
        && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any)
        && !_localAddresses.Contains(ip.ToString())
        && !(ip.IsIPv4MappedToIPv6 && _localAddresses.Contains(ip.MapToIPv4().ToString()));
    private static bool IsPhysical(MediaDevice device) => device.Kind == MediaDeviceKind.Microphone && !device.IsApplicationEndpoint
        && device.Backend is MediaBackend.DirectShow or MediaBackend.AvFoundation
        && !new[] { "BlackHole", "VB-Audio", "CABLE ", "Soundflower", "Loopback", "Virtual Audio", "NVIDIA Broadcast", "联桥共享", "LinkBridge Shared" }
            .Any(marker => (device.Name + " " + device.Id).Contains(marker, StringComparison.OrdinalIgnoreCase));
    private void Track(Task task, HashSet<Task> set)
    {
        lock (_gate) set.Add(task);
        _ = task.ContinueWith(_ => { lock (_gate) set.Remove(task); }, TaskScheduler.Default);
    }
    private void Status(string message)
    {
        lock (_gate) { if (_disposed) return; }
        try { StatusChanged?.Invoke(message); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
    }
    private static void Cancel(CancellationTokenSource cancellation)
    { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
    private void ReportSourceEnded(string address, int port)
    {
        try { _control.SourceEnded(address, port); }
        catch (Exception ex) { Status("麦克风租约撤销失败：" + ex.Message); }
        finally { lock (_gate) _sourceReportsPending = Math.Max(0, _sourceReportsPending - 1); }
    }
    private sealed class MicrophoneSourceRoute
    {
        public string Address { get; } public int Port { get; } public long Epoch { get; }
        public MediaDevice Device { get; } public IMicrophoneSenderHandle Sender { get; }
        public CancellationTokenSource Cancellation { get; } public CancellationToken Token { get; }
        public TaskCompletionSource StartCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MicrophoneSourceRoute(string address, int port, long epoch, MediaDevice device,
            IMicrophoneSenderHandle sender, CancellationToken lifetime)
        { Address = address; Port = port; Epoch = epoch; Device = device; Sender = sender;
          Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Token = Cancellation.Token; }
    }
    private sealed class MicrophoneReceiveRoute
    {
        public string Address { get; } public long Epoch { get; }
        public CancellationTokenSource Cancellation { get; } public CancellationToken Token { get; }
        public MicrophoneReceiveRoute(string address, long epoch, CancellationToken lifetime)
        { Address = address; Epoch = epoch; Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Token = Cancellation.Token; }
    }

}

internal interface IMicrophoneControl
{
    event Func<string, int, Task<bool>>? StartRequested;
    event Action<string>? StopRequested;
    string LocalPlatform { get; }
    bool HasInput { get; set; }
    IReadOnlyList<PeerInfo> Snapshot();
    Task<bool> Prepare(string address, int port, CancellationToken ct);
    Task<bool> Stop(string address, CancellationToken ct);
    bool SourceEnded(string address, int port);
}
internal interface IMicrophoneCoordinatorFactory
{
    SharedMicrophoneEndpoint Diagnose();
    IMicrophoneSenderHandle CreateSender(int port);
    IMicrophoneReceiverHandle CreateReceiver(string endpoint, Func<string, bool> isPeer, Func<bool> permit);
    IMicrophoneDemandHandle CreateDemand(Action<MicrophoneDemandSnapshot> callback);
}
internal interface IMicrophoneSenderHandle : IAsyncDisposable
{
    event Action? StreamEnded;
    bool IsRunning { get; }
    long SentBytes { get; }
    Task StartAsync(string address, MediaDevice device, CancellationToken ct);
    ValueTask StopAsync();
}
internal interface IMicrophoneReceiverHandle : IAsyncDisposable
{
    event Action<bool>? ConnectionChanged;
    bool IsConnected { get; }
    int Port { get; }
    void Start();
    void Disconnect();
}
internal interface IMicrophoneDemandHandle : IDisposable { void Start(string endpoint); }
internal sealed class DiscoveryMicrophoneControl(PeerDiscovery discovery) : IMicrophoneControl
{
    public event Func<string, int, Task<bool>>? StartRequested { add => discovery.SharedMicrophoneStartRequested += value; remove => discovery.SharedMicrophoneStartRequested -= value; }
    public event Action<string>? StopRequested { add => discovery.SharedMicrophoneStopRequested += value; remove => discovery.SharedMicrophoneStopRequested -= value; }
    public string LocalPlatform => PlatformInfo.DisplayName;
    public bool HasInput { get => discovery.HasSharedMicrophoneInput; set => discovery.HasSharedMicrophoneInput = value; }
    public IReadOnlyList<PeerInfo> Snapshot() => discovery.Snapshot();
    public Task<bool> Prepare(string address, int port, CancellationToken ct) => discovery.PrepareRemoteMicrophoneAsync(address, port, ct);
    public Task<bool> Stop(string address, CancellationToken ct) => discovery.StopRemoteMicrophoneAsync(address, ct);
    public bool SourceEnded(string address, int port) => discovery.ReportSharedMicrophoneSourceEnded(address, port);
}
internal sealed class NativeMicrophoneCoordinatorFactory(string secret) : IMicrophoneCoordinatorFactory
{
    public SharedMicrophoneEndpoint Diagnose() => SharedMicrophoneEndpoints.Diagnose();
    public IMicrophoneSenderHandle CreateSender(int port) => new NativeSender(secret, port);
    public IMicrophoneReceiverHandle CreateReceiver(string endpoint, Func<string, bool> peer, Func<bool> permit) => new NativeReceiver(
        new SharedMicrophoneReceiver(secret, peer, () => SharedMicrophoneEndpoints.CreateVirtualSink(endpoint), isDemandAllowed: permit));
    public IMicrophoneDemandHandle CreateDemand(Action<MicrophoneDemandSnapshot> callback) => new NativeDemand(new MicrophoneDemandMonitor(callback));
    private sealed class NativeSender(string secret, int port) : IMicrophoneSenderHandle
    {
        private readonly SharedMicrophoneSender _sender = new(port);
        public event Action? StreamEnded { add => _sender.StreamEnded += value; remove => _sender.StreamEnded -= value; }
        public bool IsRunning => _sender.IsRunning;
        public long SentBytes => _sender.SentBytes;
        public Task StartAsync(string address, MediaDevice device, CancellationToken ct) => _sender.StartAsync(address, secret, device, true, ct);
        public ValueTask StopAsync() => _sender.StopAsync();
        public ValueTask DisposeAsync() => _sender.DisposeAsync();
    }
    private sealed class NativeReceiver(SharedMicrophoneReceiver receiver) : IMicrophoneReceiverHandle
    {
        public event Action<bool>? ConnectionChanged { add => receiver.ConnectionChanged += value; remove => receiver.ConnectionChanged -= value; }
        public bool IsConnected => receiver.IsConnected;
        public int Port => receiver.Port;
        public void Start() => receiver.Start();
        public void Disconnect() => receiver.Disconnect();
        public ValueTask DisposeAsync() => receiver.DisposeAsync();
    }
    private sealed class NativeDemand(MicrophoneDemandMonitor monitor) : IMicrophoneDemandHandle
    { public void Start(string endpoint) => monitor.Start(endpoint); public void Dispose() => monitor.Dispose(); }
}
