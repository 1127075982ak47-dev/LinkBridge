using System.Text.Json;

namespace LinkBridge.App.Services;

/// <summary>Local paths only; network transfers stream file bytes, never apply peer paths.</summary>
public static class SharedClipboardFiles
{
    public const string Kind = "files";
    public const int Port = 45689;
    public static SharedClipboardContent Encode(IEnumerable<string> paths)
    {
        var content = new SharedClipboardContent(Kind, JsonSerializer.SerializeToUtf8Bytes(paths.ToArray()));
        _ = Decode(content);
        return content;
    }

    public static string[] Decode(SharedClipboardContent content)
    {
        if (content.Kind != Kind || content.Data.Length > 1024 * 1024)
            throw new ArgumentException("文件列表无效。");
        var paths = JsonSerializer.Deserialize<string[]>(content.Data);
        if (paths is null || paths.Length is < 1 or > 128 || paths.Any(path =>
                string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path)))
            throw new ArgumentException("请选择 1–128 个本机文件或文件夹。");
        return paths;
    }
}
