using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LinkBridge.Core.Security;

namespace LinkBridge.Core.FileTransfer;

/// <summary>Receives one paired drag at a time into private staging; success follows the destination commit callback.</summary>
public sealed class FileTransferServer : IAsyncDisposable
{
    private readonly IPAddress _allowedAddress;
    private readonly byte[] _secret;
    private readonly string _cacheRoot;
    private readonly Func<FileTransferManifest, string, CancellationToken, Task> _manifestAccepted;
    private readonly Func<Guid, string, CancellationToken, Task> _uploadReady;
    private readonly Func<Guid, string, Task>? _failed;
    private readonly int _port;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _singleUpload = new(1, 1);
    private readonly ConcurrentDictionary<TcpClient, Task> _clients = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<string>> _destinations = new();
    private TcpListener? _listener;
    private X509Certificate2? _certificate;
    private Task? _acceptTask;
    private int _disposed;
    public event Action<FileTransferProgress>? ProgressChanged;
    public event Action<string>? Diagnostic;
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _port;

    public FileTransferServer(string allowedAddress, string secret, string cacheRoot,
        Func<FileTransferManifest, string, CancellationToken, Task> manifestAccepted,
        Func<Guid, string, CancellationToken, Task> uploadReady, Func<Guid, string, Task>? failed = null,
        int port = FileTransferLimits.DefaultPort)
    {
        _allowedAddress = IPAddress.Parse(allowedAddress).MapToIPv6();
        _secret = PairedTlsTransport.SecretBytes(secret);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _manifestAccepted = manifestAccepted ?? throw new ArgumentNullException(nameof(manifestAccepted));
        _uploadReady = uploadReady ?? throw new ArgumentNullException(nameof(uploadReady));
        _failed = failed; _port = port;
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_listener is not null) throw new InvalidOperationException("文件接收服务已经启动。");
        FileTransferPaths.CreatePrivateDirectory(_cacheRoot);
        _certificate = PairedTlsTransport.CreateServerCertificate(FileTransferLimits.ProtocolContext);
        _listener = new TcpListener(IPAddress.IPv6Any, _port);
        _listener.Server.DualMode = true;
        _listener.Start(8);
        _acceptTask = AcceptAsync();
    }

    /// <summary>Record only paths actually committed by Finder/the native destination callback.</summary>
    public void ReportCommittedDestinations(Guid id, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count > FileTransferLimits.MaximumRoots || paths.Any(path => string.IsNullOrWhiteSpace(path) ||
                System.Text.Encoding.UTF8.GetByteCount(path) > FileTransferLimits.MaximumPathBytes))
            throw new ArgumentException("接收路径回执无效。", nameof(paths));
        _destinations[id] = paths.ToArray();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { break; }
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv6();
            if (remote is null || !remote.Equals(_allowedAddress) || _clients.Count >= 8)
            {
                client.Dispose(); continue;
            }
            client.NoDelay = true;
            // Register before executing so synchronous early failures cannot escape tracking.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _clients[client] = completion.Task;
            _ = RunTrackedAsync(client, completion);
        }
    }

    private async Task RunTrackedAsync(TcpClient client, TaskCompletionSource completion)
    {
        try { await HandleAsync(client).ConfigureAwait(false); }
        catch (Exception ex) { EmitDiagnostic("文件接收失败：" + ex.Message); }
        finally
        {
            client.Dispose(); completion.TrySetResult(); _clients.TryRemove(client, out _);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var stream = new SslStream(client.GetStream(), false);
        var ownsSlot = false; FileTransferManifest? manifest = null; string? staging = null;
        try
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                deadline.CancelAfter(PairedTlsTransport.Timeout);
                var permitted = await PairedTlsTransport.AuthenticateServerAsync(stream, _certificate!, _secret,
                    FileTransferLimits.ProtocolContext, () => ownsSlot = _singleUpload.Wait(0), deadline.Token).ConfigureAwait(false);
                if (!permitted) return;
            }
            manifest = await FileTransferWire.ReadJsonAsync<FileTransferManifest>(stream, _stop.Token).ConfigureAwait(false);
            FileTransferPaths.ValidateManifest(manifest);
            // Random suffix prevents a replayed valid transfer id from reusing an old staging tree.
            staging = Path.Combine(_cacheRoot, manifest.Id.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
            FileTransferPaths.CreatePrivateDirectory(staging);
            foreach (var entry in manifest.Entries.Where(entry => entry.IsDirectory).OrderBy(entry => entry.RelativePath.Count(character => character == '/')))
                FileTransferPaths.CreatePrivateDirectory(FileTransferPaths.ResolveWithin(staging, entry.RelativePath));
            using (var callbackDeadline = FileTransferWire.IdleToken(_stop.Token))
                await _manifestAccepted(manifest, staging, callbackDeadline.Token).ConfigureAwait(false);
            await FileTransferWire.WriteJsonAsync(stream, new FileTransferReply(true), _stop.Token).ConfigureAwait(false);
            long bytes = 0; var files = 0; var nextProgress = Environment.TickCount64;
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                EmitProgress(new(manifest.Id, 0, manifest.TotalBytes, 0, manifest.FileCount, "接收中"));
                for (var index = 0; index < manifest.Entries.Count; index++)
                {
                    var entry = manifest.Entries[index]; if (entry.IsDirectory) continue;
                    if (await FileTransferWire.ReadNumberAsync(stream, _stop.Token).ConfigureAwait(false) != (uint)index)
                        throw new InvalidDataException("文件传输次序无效。");
                    var finalPath = FileTransferPaths.ResolveWithin(staging, entry.RelativePath);
                    FileTransferPaths.RejectLinks(Path.GetDirectoryName(finalPath)!);
                    var temporaryPath = Path.Combine(staging, ".upload-" + index + "-" + Guid.NewGuid().ToString("N"));
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                        BufferSize = buffer.Length, Options = FileOptions.Asynchronous | FileOptions.SequentialScan };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    await using (var output = new FileStream(temporaryPath, options))
                    {
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long remaining = entry.Length;
                        while (remaining > 0)
                        {
                            using var deadline = FileTransferWire.IdleToken(_stop.Token);
                            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), deadline.Token).ConfigureAwait(false);
                            if (read == 0) throw new EndOfStreamException("发送端在文件完成前断开了连接。");
                            hash.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                            bytes += read; remaining -= read;
                            if (Environment.TickCount64 >= nextProgress)
                            {
                                EmitProgress(new(manifest.Id, bytes, manifest.TotalBytes, files, manifest.FileCount, "接收中"));
                                nextProgress = Environment.TickCount64 + 100;
                            }
                        }
                        var expected = new byte[32];
                        using (var deadline = FileTransferWire.IdleToken(_stop.Token))
                            await stream.ReadExactlyAsync(expected, deadline.Token).ConfigureAwait(false);
                        if (!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset()))
                            throw new InvalidDataException("文件SHA-256校验失败，此次拖放未保存。");
                        await output.FlushAsync(_stop.Token).ConfigureAwait(false);
                    }
                    FileTransferPaths.RejectLinks(Path.GetDirectoryName(finalPath)!);
                    File.Move(temporaryPath, finalPath, false);
                    files++;
                    if (Environment.TickCount64 >= nextProgress)
                    {
                        EmitProgress(new(manifest.Id, bytes, manifest.TotalBytes, files, manifest.FileCount, "接收中"));
                        nextProgress = Environment.TickCount64 + 100;
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            if (await FileTransferWire.ReadNumberAsync(stream, _stop.Token).ConfigureAwait(false) != FileTransferWire.EndMarker)
                throw new InvalidDataException("文件传输结束标记无效。");
            EmitProgress(new(manifest.Id, bytes, manifest.TotalBytes, files, manifest.FileCount, "等待放入目标文件夹"));
            using (var destinationDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                destinationDeadline.CancelAfter(FileTransferLimits.DestinationTimeout);
                var disconnected = MonitorDisconnectAsync(client, destinationDeadline);
                try { await _uploadReady(manifest.Id, staging, destinationDeadline.Token).ConfigureAwait(false); }
                finally { await destinationDeadline.CancelAsync().ConfigureAwait(false); await disconnected.ConfigureAwait(false); }
            }
            _destinations.TryRemove(manifest.Id, out var destinations);
            var receipt = new FileTransferReceipt(manifest.Id, bytes, files, destinations ?? Array.Empty<string>());
            await FileTransferWire.WriteJsonAsync(stream, new FileTransferReply(true, Receipt: receipt), _stop.Token).ConfigureAwait(false);
            EmitProgress(new(manifest.Id, bytes, manifest.TotalBytes, files, manifest.FileCount, "已保存"));
            // Successful staging is owned by the native promise integration until it has finished serving all readers.
            staging = null;
        }
        catch (Exception ex)
        {
            if (manifest is not null)
            {
                _destinations.TryRemove(manifest.Id, out _);
                if (_failed is not null)
                    try { await _failed(manifest.Id, ex.Message).ConfigureAwait(false); }
                    catch (Exception callbackError) { EmitDiagnostic("取消文件拖放失败：" + callbackError.Message); }
            }
            try
            {
                using var failureDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await FileTransferWire.WriteJsonAsync(stream, new FileTransferReply(false, ex is OperationCanceledException ? "文件拖放已取消或等待超时。" : ex.Message), failureDeadline.Token).ConfigureAwait(false);
            }
            catch { /* The client may already have canceled and closed its TLS socket. */ }
            EmitDiagnostic("文件接收失败：" + ex.Message);
        }
        finally
        {
            if (staging is not null)
            {
                try
                {
                    FileTransferPaths.RejectLinks(staging);
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                }
                catch (Exception ex) { EmitDiagnostic("未完成的接收文件清理失败：" + ex.Message); }
            }
            if (ownsSlot) _singleUpload.Release();
        }
    }

    private static async Task MonitorDisconnectAsync(TcpClient client, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0)
                { await lifetime.CancelAsync().ConfigureAwait(false); break; }
                await Task.Delay(200, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { await lifetime.CancelAsync().ConfigureAwait(false); }
    }
    private void EmitProgress(FileTransferProgress progress)
    { try { ProgressChanged?.Invoke(progress); } catch { /* Observer errors cannot damage the file stream. */ } }
    private void EmitDiagnostic(string message)
    { try { Diagnostic?.Invoke(message); } catch { } }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false); _listener?.Stop();
        if (_acceptTask is not null) await _acceptTask.ConfigureAwait(false);
        foreach (var client in _clients.Keys) client.Dispose();
        await Task.WhenAll(_clients.Values).ConfigureAwait(false);
        _certificate?.Dispose(); CryptographicOperations.ZeroMemory(_secret);
        _singleUpload.Dispose(); _stop.Dispose();
    }
}
