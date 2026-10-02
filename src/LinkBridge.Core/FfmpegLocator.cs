namespace LinkBridge.Core;

/// <summary>在本机定位可用的 ffmpeg 可执行文件。找不到时返回 null，调用方需给出提示。</summary>
public static class FfmpegLocator
{
    private static bool _probed;
    private static string? _resolved;

    /// <summary>ffmpeg 完整路径；未找到为 null。</summary>
    public static string? ExecutablePath
    {
        get
        {
            if (!_probed)
            {
                _resolved = Resolve();
                _probed = true;
            }

            return _resolved;
        }
    }

    public static bool Found => ExecutablePath is not null;

    private static string? Resolve()
    {
        var exe = PlatformInfo.IsWindows ? "ffmpeg.exe" : "ffmpeg";

        // 跟着程序走：发布时把 ffmpeg 放在 exe 同目录即可自带
        var beside = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(beside))
        {
            return beside;
        }

        var onPath = FindOnPath(exe);
        if (onPath is not null)
        {
            return onPath;
        }

        foreach (var candidate in KnownLocations(exe))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindOnPath(string exe)
    {
        var raw = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        foreach (var dir in raw.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                var full = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // 路径非法直接跳过
            }
        }

        return null;
    }

    private static IEnumerable<string> KnownLocations(string exe)
    {
        if (PlatformInfo.IsWindows)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", exe);
            yield return Path.Combine(@"C:\ffmpeg\bin", exe);
            yield return Path.Combine(@"C:\Program Files\ffmpeg\bin", exe);
        }
        else if (PlatformInfo.IsMacOS)
        {
            yield return "/opt/homebrew/bin/" + exe;
            yield return "/usr/local/bin/" + exe;
            yield return "/usr/bin/" + exe;
        }
    }
}
