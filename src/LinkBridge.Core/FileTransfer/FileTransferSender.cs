using System.Buffers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using LinkBridge.Core.Security;

namespace LinkBridge.Core.FileTransfer;

/// <summary>Streams source files once, hashing the bytes actually sent; source files are never moved or deleted.</summary>
public sealed class FileTransferSender
{
    public static FileTransferManifest CreateManifest(IReadOnlyList<string> paths, Guid? id = null) =>
        BuildPlan(paths, id ?? Guid.NewGuid()).Manifest;

    public Task<FileTransferReceipt> SendAsync(string address, string secret, IReadOnlyList<string> paths,
        IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default,
        int port = FileTransferLimits.DefaultPort) => SendPlanAsync(address, secret, BuildPlan(paths, Guid.NewGuid()), progress, ct, port);

    public Task<FileTransferReceipt> SendAsync(string address, string secret, FileTransferManifest manifest,
        IReadOnlyList<string> paths, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default,
        int port = FileTransferLimits.DefaultPort)
    {
        FileTransferPaths.ValidateManifest(manifest);
        var plan = BuildPlan(paths, manifest.Id);
        if (!manifest.Entries.SequenceEqual(plan.Manifest.Entries) || !manifest.Roots.SequenceEqual(plan.Manifest.Roots))
            throw new IOException("所选文件在开始传输前发生了变化，请重新拖动。");
        return SendPlanAsync(address, secret, plan, progress, ct, port);
    }

    private static SourcePlan BuildPlan(IReadOnlyList<string> paths, Guid id)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count is < 1 or > FileTransferLimits.MaximumRoots) throw new ArgumentException("请选择有效数量的文件。", nameof(paths));
        var roots = new List<FileTransferRoot>(); var entries = new List<FileTransferEntry>();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueSources = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            FileTransferPaths.RejectLinks(full);
            if (!uniqueSources.Add(full)) throw new IOException("文件被重复选择。");
            var attributes = File.GetAttributes(full);
            var directory = (attributes & FileAttributes.Directory) != 0;
            var originalName = Path.GetFileName(full);
            FileTransferPaths.ValidateRelativePath(originalName);
            var name = originalName; var collision = 1;
            while (!rootNames.Add(name))
            {
                collision++;
                name = directory ? $"{originalName} ({collision})" :
                    $"{Path.GetFileNameWithoutExtension(originalName)} ({collision}){Path.GetExtension(originalName)}";
            }
            roots.Add(new FileTransferRoot(name, directory));
            Add(full, name, attributes);
        }
        var manifest = new FileTransferManifest(id, entries.ToArray(), roots.ToArray());
        FileTransferPaths.ValidateManifest(manifest);
        return new SourcePlan(manifest, files);

        void Add(string full, string relative, FileAttributes attributes)
        {
            if (entries.Count >= FileTransferLimits.MaximumEntries) throw new IOException("文件项目数量超过传输上限。");
            FileTransferPaths.RejectLinks(full);
            FileTransferPaths.ValidateRelativePath(relative);
            var directory = (attributes & FileAttributes.Directory) != 0;
            if (directory)
            {
                entries.Add(new FileTransferEntry(relative, true, 0));
                foreach (var child in Directory.EnumerateFileSystemEntries(full).Order(StringComparer.Ordinal))
                    Add(child, relative + "/" + Path.GetFileName(child), File.GetAttributes(child));
            }
            else
            {
                var info = new FileInfo(full);
                if (info.Length > FileTransferLimits.MaximumFileBytes) throw new IOException("单个文件超过传输上限。");
                entries.Add(new FileTransferEntry(relative, false, info.Length));
                files.Add(relative, full);
            }
        }
    }

    private static async Task<FileTransferReceipt> SendPlanAsync(string address, string secret, SourcePlan plan,
        IProgress<FileTransferProgress>? progress, CancellationToken ct, int port)
    {
        var secretBytes = PairedTlsTransport.SecretBytes(secret);
        using var client = new TcpClient { NoDelay = true };
        var manifest = plan.Manifest;
        try
        {
            progress?.Report(new(manifest.Id, 0, manifest.TotalBytes, 0, manifest.FileCount, "连接中"));
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(PairedTlsTransport.Timeout);
                await client.ConnectAsync(address, port, deadline.Token).ConfigureAwait(false);
            }
            using var stream = new SslStream(client.GetStream(), false);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(PairedTlsTransport.Timeout);
                await PairedTlsTransport.AuthenticateClientAsync(stream, secretBytes, FileTransferLimits.ProtocolContext, deadline.Token).ConfigureAwait(false);
            }
            await FileTransferWire.WriteJsonAsync(stream, manifest, ct).ConfigureAwait(false);
            var accepted = await FileTransferWire.ReadJsonAsync<FileTransferReply>(stream, ct).ConfigureAwait(false);
            if (!accepted.Accepted) throw new IOException(accepted.Error ?? "对方没有接受此次文件拖放。");
            long bytes = 0; var files = 0; var nextProgress = Environment.TickCount64;
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                progress?.Report(new(manifest.Id, bytes, manifest.TotalBytes, files, manifest.FileCount, "传输中"));
                for (var index = 0; index < manifest.Entries.Count; index++)
                {
                    var entry = manifest.Entries[index];
                    if (entry.IsDirectory) continue;
                    var source = plan.Files[entry.RelativePath];
                    FileTransferPaths.RejectLinks(source);
                    var lastWrite = File.GetLastWriteTimeUtc(source);
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                        buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    FileTransferPaths.RejectLinks(source);
                    if (input.Length != entry.Length) throw new IOException("文件大小已变化，请重新拖动。");
                    await FileTransferWire.WriteNumberAsync(stream, (uint)index, ct).ConfigureAwait(false);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var remaining = entry.Length;
                    while (remaining > 0)
                    {
                        using var deadline = FileTransferWire.IdleToken(ct);
                        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), deadline.Token).ConfigureAwait(false);
                        if (read == 0) throw new IOException("源文件在传输过程中被截断。");
                        hash.AppendData(buffer, 0, read);
                        await stream.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                        bytes += read; remaining -= read;
                        if (Environment.TickCount64 >= nextProgress)
                        {
                            progress?.Report(new(manifest.Id, bytes, manifest.TotalBytes, files, manifest.FileCount, "传输中"));
                            nextProgress = Environment.TickCount64 + 100;
                        }
                    }
                    FileTransferPaths.RejectLinks(source);
                    if (input.Length != entry.Length || File.GetLastWriteTimeUtc(source) != lastWrite)
                        throw new IOException("源文件在传输过程中被修改，请重新拖动。");
                    using (var deadline = FileTransferWire.IdleToken(ct))
                        await stream.WriteAsync(hash.GetHashAndReset(), deadline.Token).ConfigureAwait(false);
                    files++;
                    if (Environment.TickCount64 >= nextProgress)
                    {
                        progress?.Report(new(manifest.Id, bytes, manifest.TotalBytes, files, manifest.FileCount, "传输中"));
                        nextProgress = Environment.TickCount64 + 100;
                    }
                }
                await FileTransferWire.WriteNumberAsync(stream, FileTransferWire.EndMarker, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                progress?.Report(new(manifest.Id, bytes, manifest.TotalBytes, files, manifest.FileCount, "等待放入目标文件夹"));
                using var destinationDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                destinationDeadline.CancelAfter(FileTransferLimits.DestinationTimeout);
                // Destination commit can legitimately take longer than the transfer's network idle timeout.
                var response = await ReadCommitReplyAsync(stream, destinationDeadline.Token).ConfigureAwait(false);
                if (!response.Accepted || response.Receipt is not { } receipt)
                    throw new IOException(response.Error ?? "对方没有完成文件保存。");
                if (receipt.Id != manifest.Id || receipt.Bytes != bytes || receipt.Files != files)
                    throw new InvalidDataException("接收完成回执与发送内容不一致。");
                progress?.Report(new(manifest.Id, bytes, manifest.TotalBytes, files, manifest.FileCount, "已保存"));
                return receipt;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        finally { CryptographicOperations.ZeroMemory(secretBytes); }
    }

    private static async Task<FileTransferReply> ReadCommitReplyAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, ct).ConfigureAwait(false);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length is < 1 or > FileTransferLimits.MaximumManifestBytes) throw new InvalidDataException("完成回执长度无效。");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Deserialize<FileTransferReply>(bytes, FileTransferWire.JsonOptions)
            ?? throw new InvalidDataException("完成回执无效。");
    }

    private sealed record SourcePlan(FileTransferManifest Manifest, IReadOnlyDictionary<string, string> Files);
}
