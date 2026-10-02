using System.Collections.Concurrent;
using System.Threading.Channels;
using LinkBridge.Core.Discovery;
using LinkBridge.Media.Camera;

namespace LinkBridge.App.Services;

/// <summary>Camera demand and four-second source leases run independently of input hooks and UI ticks.</summary>
public sealed class SharedCameraCoordinator : IAsyncDisposable
{
    private sealed class Target(string address, CancellationToken lifetime)
    {
        public string Address { get; } = address;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public CancellationToken Token { get; private set; }
        public Target Initialize() { Token = Cancellation.Token; return this; }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class BrokerSubscription(SharedCameraBroker broker)
    {
        private readonly Dictionary<Action, Action<int>> _handlers = [];
        public void Subscribe(Action handler) { var wrapper = new Action<int>(_ => handler()); _handlers[handler] = wrapper; broker.ConsumersChanged += wrapper; }
        public void Unsubscribe(Action handler) { if (_handlers.Remove(handler, out var wrapper)) broker.ConsumersChanged -= wrapper; }
    }
    private sealed record Signal(Target? Target = null, long Sequence = 0, bool? Ready = null, string? Error = null);
    private readonly Func<int> _consumers;
    private readonly Func<IReadOnlyList<PeerInfo>> _peers;
    private readonly Func<string, CancellationToken, Task<bool>> _prepare, _stopRemote;
    private readonly Action<string?> _bind;
    private readonly Action<Action> _subscribe, _unsubscribe;
    private readonly CancellationTokenSource _stop;
    private readonly Channel<Signal> _signals = Channel.CreateBounded<Signal>(new BoundedChannelOptions(64)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ConcurrentDictionary<long, Task> _requests = new();
    private readonly TimeSpan _renewInterval, _requestTimeout, _pulseInterval;
    private Target? _target;
    private Task? _loop, _pulse;
    private long _nextRenew, _sequence, _lastResult;
    private int _started, _disposed;
    private string? _lastStatus;
    public event Action<string>? StatusChanged;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public SharedCameraCoordinator(PeerDiscovery discovery, SharedCameraBroker broker, CancellationToken lifetime)
        : this(discovery, broker, lifetime, new BrokerSubscription(broker)) { }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private SharedCameraCoordinator(PeerDiscovery discovery, SharedCameraBroker broker, CancellationToken lifetime, BrokerSubscription subscription)
        : this(() => broker.ConsumerCount, discovery.Snapshot,
            (address, ct) => discovery.PrepareRemoteCameraAsync(address, broker.Port, ct),
            discovery.StopRemoteCameraAsync, address => { try { broker.ExpectedSourceAddress = address; } catch (ObjectDisposedException) { } },
            subscription.Subscribe, subscription.Unsubscribe, lifetime)
    { }

    // Injectable transport and demand make lease timing testable without camera/input privileges.
    public SharedCameraCoordinator(Func<int> consumers, Func<IReadOnlyList<PeerInfo>> peers,
        Func<string, CancellationToken, Task<bool>> prepare, Func<string, CancellationToken, Task<bool>> stopRemote,
        Action<string?> bind, Action<Action> subscribe, Action<Action> unsubscribe, CancellationToken lifetime,
        TimeSpan? renewInterval = null, TimeSpan? requestTimeout = null, TimeSpan? pulseInterval = null)
    {
        _consumers = consumers; _peers = peers; _prepare = prepare; _stopRemote = stopRemote;
        _bind = bind; _subscribe = subscribe; _unsubscribe = unsubscribe;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _renewInterval = renewInterval ?? TimeSpan.FromSeconds(4);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(9);
        _pulseInterval = pulseInterval ?? TimeSpan.FromSeconds(1);
    }
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _subscribe(OnDemandChanged);
        _loop = Task.Run(RunAsync); _pulse = Task.Run(PulseAsync); OnDemandChanged();
    }
    private void OnDemandChanged() => _signals.Writer.TryWrite(new Signal());
    private async Task PulseAsync()
    {
        using var timer = new PeriodicTimer(_pulseInterval);
        try { while (await timer.WaitForNextTickAsync(_stop.Token)) OnDemandChanged(); }
        catch (OperationCanceledException) { }
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var signal in _signals.Reader.ReadAllAsync(_stop.Token))
            {
                if (_stop.IsCancellationRequested) break;
                var demand = _consumers();
                var peer = demand > 0 ? _peers().FirstOrDefault(p =>
                    p.Platform.Contains("Mac", StringComparison.OrdinalIgnoreCase) && p.Cameras.Length > 0) : null;
                if (peer is null)
                {
                    await ResetAsync();
                    if (demand > 0) Status("应用已调用 · 正在等待 Mac 摄像头");
                    continue;
                }
                if (_target?.Address != peer.Address)
                {
                    await ResetAsync();
                    if (_stop.IsCancellationRequested) break;
                    _target = new Target(peer.Address, _stop.Token).Initialize(); _nextRenew = 0; _lastResult = 0;
                }
                if (signal.Target is not null && ReferenceEquals(signal.Target, _target)
                    && signal.Sequence >= _lastResult)
                {
                    _lastResult = signal.Sequence;
                    if (signal.Ready == true) _bind(_target.Address);
                    else if (signal.Error is not null) Status("摄像头控制等待重试：" + signal.Error);
                    else if (signal.Ready == false) Status("Mac 摄像头未确认原生 4K · 请检查权限");
                }
                // A slow/lost READY must not hold the lease scheduler. At most three
                // bounded requests overlap, each carrying a fresh nonce in discovery.
                if (Environment.TickCount64 >= _nextRenew && _requests.Count < 3)
                {
                    _nextRenew = Environment.TickCount64 + (long)_renewInterval.TotalMilliseconds;
                    var target = _target; var sequence = ++_sequence;
                    var task = Task.Run(() => RequestAsync(target, sequence));
                    _requests[sequence] = task;
                    _ = task.ContinueWith(_ => _requests.TryRemove(sequence, out var ignored), TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Status("共享摄像头调度失败：" + ex.Message); }
        finally { await ResetAsync(); }
    }
    private async Task RequestAsync(Target target, long sequence)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, target.Token);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            var ready = await _prepare(target.Address, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            _signals.Writer.TryWrite(new Signal(target, sequence, ready));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested || target.Token.IsCancellationRequested) { }
        catch (Exception ex) { _signals.Writer.TryWrite(new Signal(target, sequence, false, ex.Message)); }
    }
    private async Task ResetAsync()
    {
        var previous = _target; _target = null; _nextRenew = 0;
        _bind(null);
        if (previous is null) return;
        previous.Cancellation.Cancel();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _stopRemote(previous.Address, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (Exception ex) { if (!_stop.IsCancellationRequested) Status("摄像头停止确认等待超时：" + ex.Message); }
        finally { previous.Cancellation.Dispose(); }
    }
    private void Status(string value)
    {
        if (_lastStatus == value || _stop.IsCancellationRequested) return;
        _lastStatus = value; try { StatusChanged?.Invoke(value); } catch { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _unsubscribe(OnDemandChanged); _stop.Cancel(); _bind(null); _signals.Writer.TryComplete();
        var tasks = _requests.Values.Concat(new[] { _loop, _pulse }.OfType<Task>()).ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        finally { if (tasks.All(t => t.IsCompleted)) _stop.Dispose(); }
    }
}
