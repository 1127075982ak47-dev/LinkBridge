using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using LinkBridge.Core.Security;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LinkBridge.Media;

/// <summary>Paired 48kHz/16bit/stereo PCM playback with transactional output replacement.</summary>
public sealed class SharedAudioReceiver : IAsyncDisposable
{
    public const int Port = 45681;
    private readonly byte[] _secret;
    private readonly X509Certificate2 _certificate;
    private readonly Func<string, bool> _isPeer;
    private readonly Func<string> _outputId;
    private readonly CancellationTokenSource _cts = new();
    private readonly TcpListener _listener = new(IPAddress.Any, Port);
    private readonly object _gate = new();
    private Task? _task;
    private TcpClient? _client;
    private WasapiOut? _player;
    private MMDevice? _endpoint;
    private BufferedWaveProvider? _buffer;
    private bool _started, _disposed, _connected, _hasPcm;
    private volatile bool _outputNeedsRetry;
    private string? _actualOutputId, _actualOutputName, _outputSwitchError;
    private long _receivedBytes;
    public event Action<string>? LogEmitted;
    public event Action<bool>? ConnectionChanged;
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public bool OutputNeedsRetry => _outputNeedsRetry;
    /// <summary>Committed playback endpoint, independent of the user's requested endpoint.</summary>
    public string? ActualOutputId { get { lock (_gate) return _actualOutputId; } }
    public string? ActualOutputName { get { lock (_gate) return _actualOutputName; } }
    public string? OutputSwitchError { get { lock (_gate) return _outputSwitchError; } }
    public bool IsPlaying { get { lock (_gate) return !_disposed && _hasPcm && _player is { PlaybackState: PlaybackState.Playing }; } }

    public SharedAudioReceiver(string secret, Func<string, bool> isPeer, Func<string> outputId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (secret.Length != 32) throw new ArgumentException("音频配对密钥未配置。");
        _secret = PairedTlsTransport.SecretBytes(secret); _isPeer = isPeer; _outputId = outputId;
        _certificate = PairedTlsTransport.CreateServerCertificate("LBA2");
    }
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _listener.Start(8); _started = true;
            _task = RunAsync(_cts.Token);
        }
        LogEmitted?.Invoke("[LinkBridge] 共享音响接收已就绪，端口 45681 · LBA2 TLS");
    }
    public void RefreshOutput()
    {
        lock (_gate)
        {
            if (_disposed || _buffer is null) return;
            OpenOutput();
            SetConnected(_hasPcm && _player is { PlaybackState: PlaybackState.Playing });
        }
    }
    private bool OpenOutput()
    {
        WasapiOut? candidate = null;
        MMDevice? endpoint = null;
        WasapiOut? pausedPrevious = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var id = _outputId();
            endpoint = id.Length == 0 ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(id);
            var openedId = endpoint.ID;
            var openedName = endpoint.FriendlyName;
            candidate = new WasapiOut(endpoint, AudioClientShareMode.Shared, false, 80);
            candidate.Init(_buffer!);
            var observed = candidate;
            candidate.PlaybackStopped += (_, e) => _ = Task.Run(() =>
            {
                lock (_gate)
                {
                    if (_disposed || !ReferenceEquals(_player, observed)) return;
                    _outputNeedsRetry = true;
                    _actualOutputId = null; _actualOutputName = null;
                    _outputSwitchError = "音响输出已停止。" + (e.Exception?.Message ?? "");
                    SetConnected(false);
                    LogEmitted?.Invoke("[提示] 音响输出已停止，正在等待重试。" + (e.Exception?.Message ?? ""));
                }
            });
            // Avoid two output callbacks consuming the same PCM queue during the swap.
            if (_player is { PlaybackState: PlaybackState.Playing })
            {
                pausedPrevious = _player;
                pausedPrevious.Pause();
            }
            candidate.Play();
            var old = _player; var oldEndpoint = _endpoint;
            _player = candidate; _endpoint = endpoint;
            _actualOutputId = openedId; _actualOutputName = openedName; _outputSwitchError = null;
            candidate = null; endpoint = null;
            _outputNeedsRetry = false;
            pausedPrevious = null;
            // The previous endpoint remains usable until the replacement has opened and started.
            DisposeOutput(old, oldEndpoint);
            LogEmitted?.Invoke($"[LinkBridge] 对端音频播放输出：{openedName}");
            return true;
        }
        catch (Exception ex)
        {
            DisposeOutput(candidate, endpoint);
            try { pausedPrevious?.Play(); } catch { }
            _outputNeedsRetry = true;
            _outputSwitchError = ex.Message;
            if (_player is not { PlaybackState: PlaybackState.Playing })
            { _actualOutputId = null; _actualOutputName = null; }
            LogEmitted?.Invoke("[提示] 音响切换失败，将重试；" +
                (_player is { PlaybackState: PlaybackState.Playing } ? "继续使用原输出。" : "当前没有可用播放输出。") + ex.Message);
            return false;
        }
    }
    private void SetConnected(bool connected)
    {
        connected &= !_disposed;
        if (_connected == connected) return;
        _connected = connected;
        ConnectionChanged?.Invoke(connected);
    }
    private static void DisposeOutput(WasapiOut? player, MMDevice? endpoint)
    {
        try { player?.Dispose(); } catch { }
        try { endpoint?.Dispose(); } catch { }
    }
    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed) { client.Dispose(); break; }
                    _client = client;
                }
                client.NoDelay = true;
                var ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                if (!_isPeer(ip)) continue;
                using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: true);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(PairedTlsTransport.Timeout);
                    if (!await PairedTlsTransport.AuthenticateServerAsync(stream, _certificate, _secret, "LBA2", () =>
                    {
                        // A positive paired proof now means the requested physical output
                        // is actually open. Mac must not redirect its system sound into a
                        // virtual sink merely because the TCP/TLS connection succeeded.
                        lock (_gate)
                        {
                            if (_disposed) return false;
                            _buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
                            { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = false };
                            _hasPcm = false;
                            return OpenOutput();
                        }
                    },
                        timeout.Token).ConfigureAwait(false)) continue;
                }
                Interlocked.Exchange(ref _receivedBytes, 0);
                var data = new byte[3840];
                var assembler = new PcmFrameAssembler();
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                while (!ct.IsCancellationRequested)
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(8));
                    var count = await stream.ReadAsync(data, idle.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    lock (_gate)
                    {
                        if (_disposed || _buffer is null) break;
                        if (assembler.Push(_buffer, data, 0, count) != 0) _hasPcm = true;
                        SetConnected(_hasPcm && _player is { PlaybackState: PlaybackState.Playing });
                    }
                    Interlocked.Add(ref _receivedBytes, count);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { LogEmitted?.Invoke("[提示] 共享音频空闲超时，已释放连接。"); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { LogEmitted?.Invoke("[提示] 共享音频通道结束：" + ex.Message); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            finally
            {
                client?.Dispose();
                lock (_gate)
                {
                    if (ReferenceEquals(_client, client)) _client = null;
                    var old = _player; var oldEndpoint = _endpoint;
                    _player = null; _endpoint = null; _buffer = null;
                    _actualOutputId = null; _actualOutputName = null;
                    _hasPcm = false; _outputNeedsRetry = false;
                    DisposeOutput(old, oldEndpoint);
                    SetConnected(false);
                }
            }
        }
    }
    public ValueTask DisposeAsync()
    {
        Task? task;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            // Complete resource shutdown before the first await in the returned operation.
            _cts.Cancel(); _client?.Dispose(); _client = null; _listener.Stop();
            var player = _player; var endpoint = _endpoint;
            _player = null; _endpoint = null; _buffer = null; _hasPcm = false;
            _actualOutputId = null; _actualOutputName = null; _outputSwitchError = null;
            _outputNeedsRetry = false;
            DisposeOutput(player, endpoint);
            SetConnected(false);
            task = _task;
        }
        return new ValueTask(FinishDisposeAsync(task));
    }
    private async Task FinishDisposeAsync(Task? task)
    {
        try { if (task is not null) await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { LogEmitted?.Invoke("[提示] 音频接收清理超时；连接与播放资源已关闭。"); }
        finally
        {
            if (task is null || task.IsCompleted) DisposeSecurity();
            else _ = task.ContinueWith(_ => DisposeSecurity(), TaskScheduler.Default);
        }
    }
    private void DisposeSecurity()
    {
        _certificate.Dispose();
        CryptographicOperations.ZeroMemory(_secret);
        _cts.Dispose();
    }}



