namespace LinkBridge.Core.FileTransfer;

public sealed record FileTransferEntry(string RelativePath, bool IsDirectory, long Length);
public sealed record FileTransferRoot(string Name, bool IsDirectory);
public sealed record FileTransferManifest(Guid Id, IReadOnlyList<FileTransferEntry> Entries, IReadOnlyList<FileTransferRoot> Roots)
{
    public long TotalBytes => Entries?.Sum(entry => entry.Length) ?? 0;
    public int FileCount => Entries?.Count(entry => !entry.IsDirectory) ?? 0;
}
public sealed record FileTransferProgress(Guid Id, long Bytes, long TotalBytes, int Files, int TotalFiles, string Phase);
public sealed record FileTransferReceipt(Guid Id, long Bytes, int Files, IReadOnlyList<string> Destinations);

public static class FileTransferLimits
{
    public const int DefaultPort = 45686;
    public const string ProtocolContext = "LBF1";
    public const int MaximumEntries = 16384;
    public const int MaximumRoots = 128;
    public const int MaximumPathBytes = 4096;
    public const int MaximumManifestBytes = 4 * 1024 * 1024;
    public const long MaximumFileBytes = 64L * 1024 * 1024 * 1024;
    public const long MaximumTotalBytes = 256L * 1024 * 1024 * 1024;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan DestinationTimeout = TimeSpan.FromMinutes(10);
}
