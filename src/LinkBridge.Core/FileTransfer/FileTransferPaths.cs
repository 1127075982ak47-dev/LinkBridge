using System.Text;

namespace LinkBridge.Core.FileTransfer;

/// <summary>Portable names and explicit directories prevent path traversal and ambiguous file trees.</summary>
public static class FileTransferPaths
{
    public static void ValidateManifest(FileTransferManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Id == Guid.Empty || manifest.Entries is null || manifest.Roots is null ||
            manifest.Entries.Count is < 1 or > FileTransferLimits.MaximumEntries ||
            manifest.Roots.Count is < 1 or > FileTransferLimits.MaximumRoots)
            throw new InvalidDataException("文件列表数量无效或超过上限。");
        var roots = new Dictionary<string, FileTransferRoot>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in manifest.Roots)
        {
            if (root is null) throw new InvalidDataException("根目录信息无效。");
            ValidateRelativePath(root.Name);
            if (root.Name.Contains('/') || !roots.TryAdd(root.Name, root))
                throw new InvalidDataException("重复或无效的文件名。");
        }
        var tree = new Dictionary<string, FileTransferEntry>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var entry in manifest.Entries)
        {
            if (entry is null) throw new InvalidDataException("文件信息无效。");
            ValidateRelativePath(entry.RelativePath);
            if (entry.Length < 0 || entry.Length > FileTransferLimits.MaximumFileBytes ||
                (entry.IsDirectory && entry.Length != 0) || !tree.TryAdd(entry.RelativePath, entry))
                throw new InvalidDataException("文件大小、类型或重复路径无效。");
            bytes = checked(bytes + entry.Length);
            if (bytes > FileTransferLimits.MaximumTotalBytes)
                throw new InvalidDataException("文件总大小超过传输上限。");
            var name = entry.RelativePath.Split('/')[0];
            if (!roots.ContainsKey(name)) throw new InvalidDataException("文件不属于所选项目。");
        }
        foreach (var root in roots.Values)
            if (!tree.TryGetValue(root.Name, out var entry) || entry.IsDirectory != root.IsDirectory)
                throw new InvalidDataException("根目录与文件列表不一致。");
        foreach (var entry in tree.Values)
        {
            var slash = entry.RelativePath.LastIndexOf('/');
            if (slash < 0) continue;
            var parent = entry.RelativePath[..slash];
            if (!tree.TryGetValue(parent, out var parentEntry) || !parentEntry.IsDirectory)
                throw new InvalidDataException("文件的父目录缺失或不是目录。");
        }
    }

    public static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Encoding.UTF8.GetByteCount(path) > FileTransferLimits.MaximumPathBytes ||
            path[0] == '/' || path.Contains('\\') || path.Contains('\0'))
            throw new InvalidDataException("文件路径无效。");
        var segments = path.Split('/');
        if (segments.Length > 128) throw new InvalidDataException("文件夹层级过深。");
        foreach (var segment in segments)
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." || segment.EndsWith('.') ||
                segment.EndsWith(' ') || segment.Any(character => char.IsControl(character) || ":*?\"<>|".Contains(character)))
                throw new InvalidDataException("文件名含有两端无法安全使用的字符。");
            var basename = segment.Split('.')[0];
            if (basename.Equals("CON", StringComparison.OrdinalIgnoreCase) || basename.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                basename.Equals("AUX", StringComparison.OrdinalIgnoreCase) || basename.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (basename.Length == 4 && (basename.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                    basename.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && basename[3] is >= '1' and <= '9'))
                throw new InvalidDataException("文件名为保留的系统设备名称。");
        }
    }

    public static string ResolveWithin(string directory, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(root, comparison)) throw new InvalidDataException("文件路径越出了接收目录。");
        return target;
    }

    public static void RejectLinks(string path)
    {
        // Check ancestors too: a symlink selected via its parent must never be traversed.
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("为保留真实文件边界，不传输符号链接或重解析点。");
            }
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
    }

    internal static void CreatePrivateDirectory(string path)
    {
        RejectLinks(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        RejectLinks(path);
    }
}
