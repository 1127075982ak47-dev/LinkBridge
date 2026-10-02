using LinkBridge.Core.Discovery;
using LinkBridge.Media;
using LinkBridge.Media.Audio;

namespace LinkBridge.App.Services;

public sealed record SharedSpeakerState(long Revision, string Message, bool IsSending, bool ManuallyOverridden = false);

/// <summary>Speaker pooling and recovery run independently of UI timers. A
/// positive Start result comes only from LBA2 output/PCM/route readiness.</summary>
public sealed class SharedSpeakerCoordinator : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<PeerInfo>> _peers;
    private readonly Func<bool> _backendAvailable, _isRunning, _isRouteCurrent;
    private readonly Func<string?> _target, _routeError;
    private readonly Func<string, CancellationToken, Task> _start;
    private readonly Func<ValueTask> _stop;
    private readonly Action _recover;
    private readonly CancellationTokenSource _lifetime;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly TimeSpan _pulse, _retry;
    private CancellationTokenSource? _attempt;
    private Task? _loop;
    private bool _enabled, _disposed, _manuallyOverridden;
    private long _version, _nextAttempt;
    private SharedSpeakerState _state = new(0, "共享音响尚未启动", false);
    public event Action<SharedSpeakerState>? StateChanged;
    public SharedSpeakerState State { get { lock (_gate) return _state; } }

    public SharedSpeakerCoordinator(PeerDiscovery discovery, SharedAudioSender sender, string secret, CancellationToken lifetime)
        : this(discovery.Snapshot,
            MacSharedSpeakerRoute.IsBackendAvailable,
            () => sender.IsRunning, () => sender.TargetAddress, () => sender.LastRouteError,
            (address, token) => sender.StartAsync(address, secret, token), sender.StopAsync,
            sender.RecoverPendingRoute, lifetime, isRouteCurrent: () => sender.IsRoutingSystemOutput) { }

    /// <summary>Injectable control loop, exercised without settings, drivers or physical audio.</summary>
    public SharedSpeakerCoordinator(Func<IReadOnlyList<PeerInfo>> peers, Func<bool> backendAvailable,
        Func<bool> isRunning, Func<string?> target, Func<string?> routeError,
        Func<string, CancellationToken, Task> start, Func<ValueTask> stop, Action recover,
        CancellationToken lifetime, TimeSpan? pulse = null, TimeSpan? retry = null, Func<bool>? isRouteCurrent = null)
    {
        _peers = peers; _backendAvailable = backendAvailable; _isRunning = isRunning;
        _target = target; _routeError = routeError; _start = start; _stop = stop; _recover = recover;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _pulse = pulse ?? TimeSpan.FromSeconds(1); _retry = retry ?? TimeSpan.FromSeconds(30);
        if (_pulse <= TimeSpan.Zero || _retry <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pulse));
        _isRouteCurrent = isRouteCurrent ?? (() => true);
    }
    public void Start(bool enabled)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _enabled = enabled;
            _manuallyOverridden = false; _version++; _nextAttempt = 0; _attempt?.Cancel();
            _loop ??= Task.Run(RunAsync);
            Wake();
        }
    }
    public void Enable()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _enabled = true; _manuallyOverridden = false; _version++; _nextAttempt = 0;
            _attempt?.Cancel(); Wake();
            if (_loop is null || _loop.IsCompleted) _loop = Task.Run(RunAsync);
        }
    }
    public ValueTask DisableAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _enabled = false; _manuallyOverridden = false; _version++; _attempt?.Cancel();
            Wake();
        }
        // The transport stops/returns the route before its first await.
        return _stop();
    }
    private void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { } // One pending pulse is sufficient.
    }
    private Task WaitForPulseAsync() => _wake.WaitAsync(_pulse, _lifetime.Token);
    private void Publish(string message, bool sending, long? expectedVersion = null)
    {
        SharedSpeakerState state;
        lock (_gate)
        {
            if (_disposed || _lifetime.IsCancellationRequested || expectedVersion is { } version && version != _version
                || sending && !_enabled
                || (_state.Message == message && _state.IsSending == sending && _state.ManuallyOverridden == _manuallyOverridden)) return;
            _state = state = new(_state.Revision + 1, message, sending, _manuallyOverridden);
        }
        try { StateChanged?.Invoke(state); } catch { }
    }
    private string AfterStop(string normal) => _routeError() is { Length: > 0 } error
        ? "原 Mac 输出尚未恢复：" + error : normal;
    private bool IsCurrent(long version)
    {
        lock (_gate) return !_disposed && _enabled && version == _version && !_lifetime.IsCancellationRequested;
    }
    private async Task PauseForManualRouteAsync(long version)
    {
        long pausedVersion;
        lock (_gate)
        {
            if (_disposed || !_enabled || version != _version) return;
            _enabled = false; _manuallyOverridden = true; pausedVersion = ++_version; _attempt?.Cancel();
        }
        await _stop().ConfigureAwait(false);
        Publish(AfterStop("已保留手动选择的 Mac 输出 · 自动共享已暂停"), false, pausedVersion);
    }
    private async Task RunAsync()
    {
        var recovered = false;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                bool enabled, manual; long version;
                lock (_gate) { enabled = _enabled; manual = _manuallyOverridden; version = _version; }
                try
                {
                    if (!recovered)
                    {
                        if (Environment.TickCount64 < Volatile.Read(ref _nextAttempt))
                        { await WaitForPulseAsync().ConfigureAwait(false); continue; }
                        try { _recover(); recovered = true; }
                        catch (Exception ex)
                        {
                            lock (_gate) if (version == _version)
                                Volatile.Write(ref _nextAttempt, Environment.TickCount64 + (long)_retry.TotalMilliseconds);
                            Publish("原 Mac 音响输出恢复失败：" + ex.Message, false, version);
                            await WaitForPulseAsync().ConfigureAwait(false); continue;
                        }
                    }
                    if (!enabled)
                    {
                        await _stop().ConfigureAwait(false);
                        Publish(AfterStop(manual ? "已保留手动选择的 Mac 输出 · 自动共享已暂停"
                            : "共享音响已暂停 · 保留 Mac 原输出"), false, version);
                    }
                    // Preserve a user-selected route even when the Windows peer disappears in the same pulse.
                    else if (_isRunning() && !_isRouteCurrent())
                        await PauseForManualRouteAsync(version).ConfigureAwait(false);
                    else
                    {
                        var peers = _peers();
                        var peer = peers.FirstOrDefault(p => p.Address == _target() && p.Platform.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                            ?? peers.FirstOrDefault(p => p.Platform.Contains("Windows", StringComparison.OrdinalIgnoreCase));
                        if (peer is null || !_backendAvailable())
                        {
                            await _stop().ConfigureAwait(false);
                            Publish(AfterStop(peer is null ? "等待 Windows 音响 · 保留 Mac 原输出"
                                : "共享音响不可用：未找到独立 BlackHole 2ch 输出 · 保留 Mac 原输出"), false, version);
                        }
                        else if (_isRunning() && _target() == peer.Address)
                            Publish("正在使用 Windows 音响 · Mac 系统声音已自动接入", true, version);
                        else if (Environment.TickCount64 >= Volatile.Read(ref _nextAttempt))
                        {
                            if (_routeError() is { Length: > 0 })
                            {
                                await _stop().ConfigureAwait(false);
                                if (_routeError() is { Length: > 0 } restoreError)
                                    throw new IOException("原 Mac 输出仍未恢复，未启动新的音频发送：" + restoreError);
                            }
                            CancellationTokenSource attempt;
                            lock (_gate)
                            {
                                if (_disposed || !_enabled || version != _version) continue;
                                _attempt = attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                            }
                            Publish("正在确认 Windows 实体音响与 Mac 音频首帧…", false, version);
                            try
                            {
                                await _start(peer.Address, attempt.Token).ConfigureAwait(false);
                                if (!IsCurrent(version) || attempt.IsCancellationRequested)
                                    await _stop().ConfigureAwait(false);
                                else if (!_peers().Any(p => p.Address == peer.Address && p.Platform.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                                    || !_backendAvailable())
                                {
                                    await _stop().ConfigureAwait(false);
                                    Publish(AfterStop("共享音响条件已改变 · 保留 Mac 原输出"), false, version);
                                }
                                else if (_isRunning() && !_isRouteCurrent())
                                    await PauseForManualRouteAsync(version).ConfigureAwait(false);
                                else if (_isRunning() && _target() == peer.Address)
                                    Publish("正在使用 Windows 音响 · Mac 系统声音已自动接入", true, version);
                                else throw new IOException("音频首帧后连接已结束，未切换共享状态。");
                            }
                            catch (OperationCanceledException) when (attempt.IsCancellationRequested)
                            { await _stop().ConfigureAwait(false); }
                            finally
                            {
                                lock (_gate) if (ReferenceEquals(_attempt, attempt)) _attempt = null;
                                attempt.Dispose();
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    try { await _stop().ConfigureAwait(false); } catch (Exception stopError)
                    { ex = new AggregateException(ex, stopError); }
                    lock (_gate) if (_enabled && version == _version && !_disposed)
                        Volatile.Write(ref _nextAttempt, Environment.TickCount64 + (long)_retry.TotalMilliseconds);
                    Publish(AfterStop("Windows 共享音响未就绪 · 保留 Mac 原输出：" + ex.Message), false, version);
                }
                await WaitForPulseAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Publish("共享音响协调器已停止：" + ex.Message, false); }
        finally { await _stop().ConfigureAwait(false); }
    }
    public ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true; _enabled = false; _version++; _lifetime.Cancel(); _attempt?.Cancel(); loop = _loop;
        }
        var stop = _stop();
        return new ValueTask(FinishDisposeAsync(loop, stop));
    }
    private async Task FinishDisposeAsync(Task? loop, ValueTask stop)
    {
        var tasks = loop is null ? stop.AsTask() : Task.WhenAll(loop, stop.AsTask());
        try { await tasks.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
        catch (TimeoutException) { _ = tasks.ContinueWith(_ => ReleaseLifetime(), TaskScheduler.Default); return; }
        finally { if (tasks.IsCompleted) ReleaseLifetime(); }
    }
    private void ReleaseLifetime() { _wake.Dispose(); _lifetime.Dispose(); }
}
