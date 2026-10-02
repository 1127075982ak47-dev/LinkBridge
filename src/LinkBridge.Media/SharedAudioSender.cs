using System.Diagnostics;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using LinkBridge.Core.Security;
using LinkBridge.Core;
using LinkBridge.Media.Audio;

namespace LinkBridge.Media;

/// <summary>Readiness requires an open paired output, actual local PCM and a recoverable route.</summary>
public sealed class SharedAudioSender : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _startGate = new(1);
    private readonly Func<ProcessStartInfo> _captureFactory;
    private readonly Action _takeRoute, _returnRoute, _recoverRoute;
    private readonly int _port;
    private readonly bool _requiresMac;
    private MacSharedSpeakerRoute? _route;
    private TcpClient? _client;
    private SslStream? _secureStream;
    private Process? _capture;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private TaskCompletionSource<bool>? _ready;
    private long _generation, _sentBytes;
    private bool _disposed, _readyState, _routeTaken;
    private string? _targetAddress, _routeError;
    public event Action<string>? LogEmitted;
    public event Action? StreamEnded;
    public bool IsRunning { get { lock (_gate) return _readyState && !_disposed; } }
    public bool IsRoutingSystemOutput
    {
        get { lock (_gate) return !_disposed && _readyState && _routeTaken && (!_requiresMac || _route?.IsCurrentRoute == true); }
    }
    public string? TargetAddress { get { lock (_gate) return _readyState ? _targetAddress : null; } }
    public string? LastRouteError { get { lock (_gate) return _routeError; } }
    public long SentBytes => Interlocked.Read(ref _sentBytes);
    public SharedAudioSender()
    {
        _requiresMac = true; _port = SharedAudioReceiver.Port; _captureFactory = CreateMacCapture;
        _takeRoute = () => (_route ??= new MacSharedSpeakerRoute()).Start(true, true);
        _returnRoute = () => { _route?.Stop(); };
        _recoverRoute = () => { (_route ??= new MacSharedSpeakerRoute()).RecoverPendingLease(); };
    }
    /// <summary>Isolated tests supply a synthetic process and route actions; no physical audio.</summary>
    public SharedAudioSender(Func<ProcessStartInfo> captureFactory, Action takeRoute,
        Action returnRoute, Action recoverRoute, int port)
    {
        ArgumentNullException.ThrowIfNull(captureFactory); ArgumentNullException.ThrowIfNull(takeRoute);
        ArgumentNullException.ThrowIfNull(returnRoute); ArgumentNullException.ThrowIfNull(recoverRoute);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _captureFactory = captureFactory; _takeRoute = takeRoute; _returnRoute = returnRoute;
        _recoverRoute = recoverRoute; _port = port;
    }
    public void RecoverPendingRoute()
    {
        if (_requiresMac && !OperatingSystem.IsMacOS()) return;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_capture is not null) throw new InvalidOperationException("采集期间不能恢复旧输出租约。");
            try { _recoverRoute(); _routeError = null; }
            catch (Exception ex) { _routeError = ex.Message; throw; }
        }
    }
    private static ProcessStartInfo CreateMacCapture()
    {
        var info = new ProcessStartInfo(FfmpegLocator.ExecutablePath ?? throw new InvalidOperationException("未找到 ffmpeg。"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "warning", "-nostdin", "-f", "avfoundation", "-i", ":BlackHole 2ch", "-vn", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1" })
            info.ArgumentList.Add(arg);
        return info;
    }
    public async Task StartAsync(string host, string secret, CancellationToken ct = default)
    {
        if (_requiresMac && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var key = PairedTlsTransport.SecretBytes(secret); var entered = false;
        try
        {
            await _startGate.WaitAsync(ct).ConfigureAwait(false); entered = true;
            await StopAsync().ConfigureAwait(false);
            TcpClient client; SslStream stream; CancellationToken token;
            TaskCompletionSource<bool> ready; long generation;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_routeTaken) throw new InvalidOperationException("原 Mac 输出尚未恢复，请先重试停止共享。" + _routeError);
                generation = ++_generation; client = _client = new TcpClient { NoDelay = true };
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct); token = _cts.Token;
                _targetAddress = host; _readyState = false; _routeError = null;
                _ready = ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _sentBytes, 0);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, _port, timeout.Token).ConfigureAwait(false);
            stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            lock (_gate)
            {
                if (generation != _generation || _disposed || token.IsCancellationRequested)
                { stream.Dispose(); throw new OperationCanceledException(token); }
                _secureStream = stream;
            }
            await PairedTlsTransport.AuthenticateClientAsync(stream, key, "LBA2", timeout.Token).ConfigureAwait(false);
            var capture = new Process { StartInfo = _captureFactory() };
            capture.ErrorDataReceived += (_, e) =>
            {
                lock (_gate) if (generation == _generation && !_disposed && !string.IsNullOrWhiteSpace(e.Data)) EmitLog(e.Data);
            };
            lock (_gate)
            {
                if (generation != _generation || _disposed || token.IsCancellationRequested)
                { capture.Dispose(); throw new OperationCanceledException(token); }
                _capture = capture;
                if (!capture.Start()) throw new InvalidOperationException("共享音频采集启动失败。");
                capture.BeginErrorReadLine();
                _pump = Task.Run(() => PumpAsync(capture, stream, generation, ready, token));
            }
            if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(9), token).ConfigureAwait(false))
                throw new IOException("未取得真实音频首帧；请检查 Mac 麦克风授权与 BlackHole 2ch。");
            lock (_gate)
                if (generation != _generation || !_readyState || _disposed || token.IsCancellationRequested)
                    throw new OperationCanceledException(token);
        }
        catch { if (entered) await StopAsync().ConfigureAwait(false); throw; }
        finally { CryptographicOperations.ZeroMemory(key); if (entered) _startGate.Release(); }
    }
    private async Task PumpAsync(Process capture, Stream target, long generation, TaskCompletionSource<bool> ready, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[3840];
            using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var write = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (!ct.IsCancellationRequested)
            {
                read.CancelAfter(TimeSpan.FromSeconds(8));
                var count = await capture.StandardOutput.BaseStream.ReadAsync(buffer, read.Token).ConfigureAwait(false);
                read.CancelAfter(Timeout.InfiniteTimeSpan); if (count == 0) break;
                write.CancelAfter(TimeSpan.FromSeconds(2));
                await target.WriteAsync(buffer.AsMemory(0, count), write.Token).ConfigureAwait(false);
                write.CancelAfter(Timeout.InfiniteTimeSpan);
                lock (_gate)
                {
                    if (_disposed || generation != _generation || ct.IsCancellationRequested) return;
                    var sent = Interlocked.Add(ref _sentBytes, count);
                    if (!_readyState && sent >= 4)
                    {
                        // Even a failed route can have a committed recovery record.
                        _routeTaken = true; _takeRoute(); _readyState = true; ready.TrySetResult(true);
                        EmitLog("[LinkBridge] Windows 实体音响与真实音频首帧已确认；Mac 系统声音已自动接入。");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_gate) if (generation == _generation && !ct.IsCancellationRequested) EmitLog("[错误] 音频发送结束：" + ex.Message);
        }
        finally
        {
            ProcessHelper.TryKill(capture); ready.TrySetResult(false);
            var notifyEnded = false;
            lock (_gate)
                if (generation == _generation)
                {
                    ReturnRouteLocked(); _readyState = false; _client?.Dispose(); _secureStream?.Dispose();
                    notifyEnded = !ct.IsCancellationRequested && !_disposed;
                }
            if (notifyEnded) _ = Task.Run(() =>
            {
                Action? handlers;
                lock (_gate) { if (generation != _generation || _disposed) return; handlers = StreamEnded; }
                if (handlers is not null) foreach (Action handler in handlers.GetInvocationList())
                    try { handler(); } catch (Exception ex) { Trace.WriteLine("Shared audio EOF subscriber: " + ex.Message); }
            });
        }
    }
    private void ReturnRouteLocked()
    {
        if (!_routeTaken) return;
        try { _returnRoute(); _routeTaken = false; _routeError = null; }
        catch (Exception ex) { _routeError = ex.Message; EmitLog("[错误] 原 Mac 输出尚未恢复：" + ex.Message); }
    }
    private void EmitLog(string message)
    {
        var handlers = LogEmitted;
        if (handlers is null) return;
        _ = Task.Run(() =>
        {
            foreach (Action<string> handler in handlers.GetInvocationList())
                try { handler(message); } catch (Exception ex) { Trace.WriteLine("Shared audio log subscriber: " + ex.Message); }
        });
    }
    public ValueTask StopAsync()
    {
        TcpClient? client; SslStream? stream; Process? capture; CancellationTokenSource? cancellation; Task? pump;
        lock (_gate)
        {
            ++_generation; client = _client; _client = null; stream = _secureStream; _secureStream = null;
            capture = _capture; _capture = null; cancellation = _cts; _cts = null; pump = _pump; _pump = null;
            _readyState = false; _targetAddress = null; _ready?.TrySetResult(false); _ready = null;
            cancellation?.Cancel(); client?.Dispose(); if (capture is not null) ProcessHelper.TryKill(capture);
            stream?.Dispose(); ReturnRouteLocked();
        }
        return new ValueTask(FinishStopAsync(capture, cancellation, pump));
    }
    private static async Task FinishStopAsync(Process? capture, CancellationTokenSource? cancellation, Task? pump)
    {
        try { if (pump is not null) await pump.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException)
        { _ = pump!.ContinueWith(_ => { capture?.Dispose(); cancellation?.Dispose(); }, TaskScheduler.Default); return; }
        finally { if (pump is null || pump.IsCompleted) { capture?.Dispose(); cancellation?.Dispose(); } }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        var stop = StopAsync();
        lock (_gate)
        {
            try
            {
                if (_route is not null) { _route.Dispose(); _routeTaken = false; _routeError = null; }
            }
            catch (Exception ex) { _routeError = ex.Message; EmitLog("[错误] 输出恢复记录保留：" + ex.Message); }
            _route = null;
        }
        return stop;
    }
}
