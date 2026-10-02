using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LinkBridge.Core;

namespace LinkBridge.Media;

/// <summary>Serializes receiver sessions and decodes the preview without keeping retired FFmpeg processes alive.</summary>
public sealed class MediaStreamReceiver : IDisposable
{
    private const int PreviewWidth = 960;
    private const int PreviewHeight = 540;
    private readonly object _gate = new();
    private Session? _session;
    private long _generation;
    private bool _disposed;
    private bool _listeningRequested;
    private int _requestedPort;
    private string? _expectedSourceAddress;
    private sealed class Session(Process process, CancellationTokenSource cancellation, long generation)
    {
        public readonly Process Process = process;
        public readonly CancellationTokenSource Cancellation = cancellation;
        public readonly long Generation = generation;
    }
    public event Action<string>? LogEmitted;
    public event Action? StreamEnded;
    public event Action<byte[], int, int>? FrameReady;
    public event Action<PreviewFrame>? FrameDecoded;
    public int Width => PreviewWidth;
    public int Height => PreviewHeight;
    public bool IsListeningRequested { get { lock (_gate) return _listeningRequested; } }
    /// <summary>Only a signed control request may supply the paired sender's IPv4 address.</summary>
    public string? ExpectedSourceAddress
    {
        get { lock (_gate) return _expectedSourceAddress; }
        set
        {
            var address = ValidateSource(value);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_expectedSourceAddress == address) return;
                StopDecoderLocked(); _expectedSourceAddress = address;
                if (_listeningRequested && address is not null) StartDecoderLocked();
            }
        }
    }
    public bool IsRunning
    {
        get { lock (_gate) { try { return _session is { } session && !session.Process.HasExited; } catch { return false; } } }
    }

    public void Start(int port, bool forceRestart = false)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var samePort = _listeningRequested && _requestedPort == port;
            _listeningRequested = true; _requestedPort = port;
            if (samePort && !forceRestart && IsRunning) return;
            StopDecoderLocked();
            if (_expectedSourceAddress is null) { Log("[LinkBridge] 已准备接收；等待已配对的发送端确认。"); return; }
            StartDecoderLocked();
        }
    }
    /// <summary>
    /// Atomically binds an authenticated source only to an existing receive request.
    /// A Stop racing this call wins without a later binding recreating receive intent.
    /// </summary>
    public bool TryAcceptAuthenticatedSource(string address, int port, bool forceRestart = true)
    {
        var source = ValidateSource(address) ?? throw new ArgumentNullException(nameof(address));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        lock (_gate)
        {
            if (_disposed || !_listeningRequested || _requestedPort != port) return false;
            var changed = _expectedSourceAddress != source;
            if (changed || forceRestart || !IsRunning)
            {
                StopDecoderLocked(); _expectedSourceAddress = source; StartDecoderLocked();
            }
            return IsRunning;
        }
    }
    private static string? ValidateSource(string? value)
    {
        if (value is null) return null;
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast) || ip.GetAddressBytes()[0] is 0 or >= 224)
            throw new ArgumentException("A unicast IPv4 media source is required.", nameof(value));
        return ip.ToString();
    }
    private void StartDecoderLocked()
    {
            var port = _requestedPort;
            var ffmpeg = FfmpegLocator.ExecutablePath ?? throw new InvalidOperationException("本机没有找到 ffmpeg，无法接收。");
            var info = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            // Keep normal demux buffering: nobuffer stalled real AVFoundation UDP input.
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "warning", "-nostdin",
                "-i", $"udp://0.0.0.0:{port}?listen=1&fifo_size=1000000&overrun_nonfatal=1&sources={_expectedSourceAddress}",
                "-an", "-f", "rawvideo", "-pix_fmt", "bgra", "-vf", $"scale={PreviewWidth}:{PreviewHeight}", "-" })
                info.ArgumentList.Add(argument);
            var process = new Process { StartInfo = info };
            var cancellation = new CancellationTokenSource();
            var session = new Session(process, cancellation, ++_generation);
            process.ErrorDataReceived += (_, e) =>
            {
                lock (_gate)
                    if (IsCurrent(session) && !string.IsNullOrWhiteSpace(e.Data)) Log(e.Data);
            };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("ffmpeg 接收进程启动失败。");
                _session = session;
                process.BeginErrorReadLine();
                var token = cancellation.Token;
                _ = Task.Run(() => PumpAsync(session, token));
                Log($"[LinkBridge] 已开始接收 {_expectedSourceAddress}，监听端口 {port}");
            }
            catch
            {
                if (ReferenceEquals(_session, session)) _session = null;
                ProcessHelper.TryKill(process);
                process.Dispose(); cancellation.Dispose();
                throw;
            }
    }

    public void Stop()
    {
        lock (_gate) { _listeningRequested = false; _requestedPort = 0; _expectedSourceAddress = null; StopDecoderLocked(); }
    }
    private void StopDecoderLocked()
    {
        ++_generation;
        var session = _session; _session = null;
        if (session is null) return;
        session.Cancellation.Cancel();
        ProcessHelper.TryKill(session.Process);
        // The pump owns disposal and releases its pooled frame after cancellation.
    }
    private bool IsCurrent(Session session) => !_disposed && ReferenceEquals(_session, session)
        && session.Generation == _generation && !session.Cancellation.IsCancellationRequested;
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _listeningRequested = false; _requestedPort = 0; _expectedSourceAddress = null; StopDecoderLocked(); } }
    private void Log(string message) { try { LogEmitted?.Invoke(message); } catch { } }

    private async Task PumpAsync(Session session, CancellationToken ct)
    {
        var size = PreviewWidth * PreviewHeight * 4;
        var frame = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var stream = session.Process.StandardOutput.BaseStream;
            var offset = 0;
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(frame.AsMemory(offset, size - offset), ct).ConfigureAwait(false);
                if (read == 0) break;
                offset += read;
                if (offset != size) continue;
                // Serialize delivery with Stop: no retired session can publish into a new one.
                lock (_gate)
                {
                    if (!IsCurrent(session)) break;
                    if (FrameDecoded is { } consumer)
                    {
                        var delivered = ArrayPool<byte>.Shared.Rent(size);
                        Buffer.BlockCopy(frame, 0, delivered, 0, size);
                        var lease = new PreviewFrame(delivered, PreviewWidth, PreviewHeight);
                        try { consumer(lease); } catch { lease.Dispose(); throw; }
                    }
                    if (FrameReady is { } legacy)
                    {
                        var delivered = new byte[size];
                        Buffer.BlockCopy(frame, 0, delivered, 0, size);
                        legacy(delivered, PreviewWidth, PreviewHeight);
                    }
                }
                offset = 0;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_gate) if (IsCurrent(session)) Log($"[错误] 接收中断：{ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
            lock (_gate)
            {
                ProcessHelper.TryKill(session.Process);
                if (IsCurrent(session))
                {
                    _session = null;
                    try { Log("[提示] 媒体接收进程已结束。"); StreamEnded?.Invoke(); } catch { }
                }
            }
            try { await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            session.Process.Dispose(); session.Cancellation.Dispose();
        }
    }
}


