using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.AccessControl;

namespace LinkBridge.Core;

/// <summary>用户可调的全部配置，持久化到用户目录，两个平台各用各的规范位置。</summary>
public sealed class AppSettings
{
    /// <summary>在局域网里对别人显示的名字。</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = PlatformInfo.MachineName;

    /// <summary>是否开启局域网自动发现。</summary>
    [JsonPropertyName("autoDiscover")]
    public bool AutoDiscover { get; set; } = true;

    /// <summary>媒体流接收端口。</summary>
    [JsonPropertyName("mediaPort")]
    public int MediaPort { get; set; } = 45679;

    [JsonPropertyName("enableInputServer")]
    public bool EnableInputServer { get; set; }

    [JsonPropertyName("allowedControllerAddress")]
    public string AllowedControllerAddress { get; set; } = string.Empty;

    [JsonPropertyName("inputPairingSecret")]
    public string InputPairingSecret { get; set; } = string.Empty;

    [JsonPropertyName("remoteDisplayWidth")]
    public int RemoteDisplayWidth { get; set; } = 2560;

    [JsonPropertyName("remoteDisplayHeight")]
    public int RemoteDisplayHeight { get; set; } = 1440;

    /// <summary>采集帧率。</summary>
    [JsonPropertyName("frameRate")]
    public int FrameRate { get; set; } = 30;

    /// <summary>视频码率（Mbps）。</summary>
    [JsonPropertyName("videoBitrateMbps")]
    public int VideoBitrateMbps { get; set; } = 4;

    /// <summary>音频码率（kbps）。</summary>
    [JsonPropertyName("audioBitrateKbps")]
    public int AudioBitrateKbps { get; set; } = 128;

    /// <summary>优先使用硬件编码器（显卡编码，更省 CPU）。</summary>
    [JsonPropertyName("useHardwareEncoder")]
    public bool UseHardwareEncoder { get; set; }

    /// <summary>开机自动启动。</summary>
    [JsonPropertyName("launchAtStartup")]
    public bool LaunchAtStartup { get; set; }

    /// <summary>界面用的主题：跟随系统 / 浅色 / 深色。</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    /// <summary>空字符串表示跟随系统默认；非空时固定到稳定端点 ID。</summary>
    [JsonPropertyName("playbackDeviceId")]
    public string PlaybackDeviceId { get; set; } = string.Empty;

    [JsonPropertyName("reduceMotion")]
    public bool ReduceMotion { get; set; }

    [JsonPropertyName("shareSystemAudioAutomatically")]
    public bool ShareSystemAudioAutomatically { get; set; } = true;

    [JsonPropertyName("shareClipboardAutomatically")]
    public bool ShareClipboardAutomatically { get; set; } = true;

    [JsonPropertyName("shareLocalCamera")]
    public bool ShareLocalCamera { get; set; } = true;
    [JsonPropertyName("shareLocalMicrophone")]
    public bool ShareLocalMicrophone { get; set; } = true;
    // Backend, Id and name must all match. A changed AVFoundation index cannot
    // silently select another camera after a hot-plug or a device refresh.
    [JsonPropertyName("preferredCameraSource")]
    public string PreferredCameraSource { get; set; } = string.Empty;
    [JsonPropertyName("preferredMicrophoneSource")]
    public string PreferredMicrophoneSource { get; set; } = string.Empty;
}

/// <summary>Private, atomic settings persistence. Unknown JSON fields survive older app versions.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AppSettings, Dictionary<string, JsonElement>> ExtraFields = new();
    private static readonly Dictionary<string, JsonElement> DefaultFields =
        JsonSerializer.SerializeToElement(new AppSettings(), SerializerOptions).EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    private const long MaximumFileBytes = 1024 * 1024;
    public static string FilePath { get; } = ResolvePath();
    public static string? LastLoadError { get; private set; }

    public static AppSettings Load() => LoadFromPath(FilePath);
    private static AppSettings LoadFromPath(string path)
    {
        lock (Gate)
        {
            LastLoadError = null;
            try
            {
                if (!File.Exists(path)) return new AppSettings();
                ProtectDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                ProtectFile(path);
                using var document = ReadDocument(path);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("配置根节点必须为JSON对象。");
                var fields = document.RootElement.EnumerateObject().ToDictionary(property => property.Name,
                    property => property.Value.Clone(), StringComparer.Ordinal);
                // A wrong type in one simple property must not discard valid pairing data.
                foreach (var defaults in DefaultFields)
                {
                    if (!fields.TryGetValue(defaults.Key, out var current)) continue;
                    var valid = defaults.Value.ValueKind switch
                    {
                        JsonValueKind.Number => current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out _),
                        JsonValueKind.True or JsonValueKind.False => current.ValueKind is JsonValueKind.True or JsonValueKind.False,
                        JsonValueKind.String => current.ValueKind == JsonValueKind.String,
                        _ => true
                    };
                    if (!valid) { fields[defaults.Key] = defaults.Value; LastLoadError = "部分配置类型无效，已在本次运行使用默认值；原文件未改写。"; }
                }
                var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(fields), SerializerOptions) ?? new AppSettings();
                if (NormalizeLoaded(loaded)) LastLoadError = "部分配置超出允许范围，已在本次运行使用默认值；原文件未改写。";
                var extra = fields.Where(pair => !DefaultFields.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key,
                    pair => pair.Value, StringComparer.Ordinal);
                ExtraFields.Add(loaded, extra);
                return loaded;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.Security.SecurityException)
            {
                LastLoadError = "配置读取失败，已使用默认值：" + ex.Message;
                return new AppSettings();
            }
        }
    }

    /// <summary>Compatibility API. A failed write throws rather than reporting implicit success.</summary>
    public static void Save(AppSettings settings)
    {
        if (!TrySave(settings, out var error)) throw new IOException(error ?? "配置保存失败。");
    }
    public static bool TrySave(AppSettings settings, out string? error) => TrySaveToPath(settings, FilePath, out error);
    private static bool TrySaveToPath(AppSettings settings, string path, out string? error)
    {
        error = null;
        string? temporary = null;
        lock (Gate)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(settings);
                var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, SerializerOptions), SerializerOptions)!;
                if (NormalizeLoaded(snapshot)) throw new ArgumentException("配置包含无效的端口、帧率、码率、显示尺寸、名称或主题，请修正后再保存。");
                var fullPath = Path.GetFullPath(path);
                var directory = Path.GetDirectoryName(fullPath)!;
                ProtectDirectory(directory);
                if (File.Exists(fullPath)) ProtectFile(fullPath);
                var fields = JsonSerializer.SerializeToElement(snapshot, SerializerOptions).EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
                if (ExtraFields.TryGetValue(settings, out var extras))
                    foreach (var extra in extras) fields[extra.Key] = extra.Value;
                // Current on-disk opaque values win over an older loaded snapshot.
                if (File.Exists(fullPath))
                {
                    using var previous = ReadDocument(fullPath);
                    if (previous.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var property in previous.RootElement.EnumerateObject())
                            if (!DefaultFields.ContainsKey(property.Name)) fields[property.Name] = property.Value.Clone();
                }
                var bytes = JsonSerializer.SerializeToUtf8Bytes(fields, SerializerOptions);
                if (bytes.LongLength > MaximumFileBytes) throw new IOException("配置超过1MB保存限额。");
                temporary = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    Options = FileOptions.WriteThrough, BufferSize = 4096
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                {
                    ProtectFile(temporary);
                    stream.Write(bytes); stream.Flush(flushToDisk: true);
                }
                if (OperatingSystem.IsWindows() && File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
                else File.Move(temporary, fullPath, overwrite: true);
                temporary = null;
                // The destination already carries the private temp-file ACL/mode; verify after commit.
                ProtectFile(fullPath);
                if (!OperatingSystem.IsWindows()) FlushUnixDirectory(directory);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.Security.SecurityException or NotSupportedException)
            {
                error = "配置保存失败：" + ex.Message;
                return false;
            }
            finally
            {
                if (temporary is not null) { try { File.Delete(temporary); } catch { } }
            }
        }
    }
    private static JsonDocument ReadDocument(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaximumFileBytes) throw new IOException("配置超过1MB读取限额。");
                return JsonDocument.Parse(stream);
            }
            catch (IOException ex) when (OperatingSystem.IsWindows() && attempt < 10 && (ex.HResult & 0xFFFF) is 32 or 33)
            {
                // NTFS replacement can briefly exclude an independent reader; never turn
                // that transient sharing violation into a fresh default pairing config.
                Thread.Sleep(10);
            }
        }
    }
    private static bool NormalizeLoaded(AppSettings settings)
    {
        var changed = false;
        var defaults = new AppSettings();
        if (string.IsNullOrWhiteSpace(settings.DisplayName) || settings.DisplayName.Length > 80 || settings.DisplayName.Any(char.IsControl))
        { settings.DisplayName = defaults.DisplayName; changed = true; }
        if (settings.MediaPort is < 1025 or > 65535) { settings.MediaPort = defaults.MediaPort; changed = true; }
        if (settings.FrameRate is < 1 or > 120) { settings.FrameRate = defaults.FrameRate; changed = true; }
        if (settings.VideoBitrateMbps is < 1 or > 200) { settings.VideoBitrateMbps = defaults.VideoBitrateMbps; changed = true; }
        if (settings.AudioBitrateKbps is < 32 or > 1024) { settings.AudioBitrateKbps = defaults.AudioBitrateKbps; changed = true; }
        if (settings.RemoteDisplayWidth is < 320 or > 16384) { settings.RemoteDisplayWidth = defaults.RemoteDisplayWidth; changed = true; }
        if (settings.RemoteDisplayHeight is < 200 or > 16384) { settings.RemoteDisplayHeight = defaults.RemoteDisplayHeight; changed = true; }
        if (settings.Theme is not ("system" or "light" or "dark")) { settings.Theme = defaults.Theme; changed = true; }
        return changed;
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("配置文件或所属目录不能为符号链接/重解析点。");
    }
    private static void ProtectDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        if (OperatingSystem.IsWindows())
        {
            if (PrivateWindowsAcl(new DirectoryInfo(directory).GetAccessControl())) return;
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var identity in WindowsPrincipals())
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void ProtectFile(string path)
    {
        RejectLink(path);
        if (OperatingSystem.IsWindows())
        {
            if (PrivateWindowsAcl(new FileInfo(path).GetAccessControl())) return;
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var identity in WindowsPrincipals())
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity,
                    System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool PrivateWindowsAcl(System.Security.AccessControl.FileSystemSecurity security)
    {
        if (!security.AreAccessRulesProtected) return false;
        var expected = WindowsPrincipals().Select(identity => identity.Value).ToHashSet(StringComparer.Ordinal);
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToArray();
        return rules.All(rule => !rule.IsInherited && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow &&
            (rule.FileSystemRights & System.Security.AccessControl.FileSystemRights.FullControl) == System.Security.AccessControl.FileSystemRights.FullControl)
            && rules.Select(rule => rule.IdentityReference.Value).ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static System.Security.Principal.SecurityIdentifier[] WindowsPrincipals() =>
    [System.Security.Principal.WindowsIdentity.GetCurrent().User ?? throw new IOException("无法确定当前用户SID。"),
        new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
        new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null)];
    private static void FlushUnixDirectory(string directory)
    {
        var handle = OpenUnix(directory, 0);
        if (handle < 0) throw new IOException("配置目录无法同步，errno " + System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        try
        {
            if (SyncUnix(handle) != 0) throw new IOException("配置目录同步失败，errno " + System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        }
        finally { CloseUnix(handle); }
    }
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int SyncUnix(int descriptor);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close")]
    private static extern int CloseUnix(int descriptor);
    private static string ResolvePath()
    {
        if (PlatformInfo.IsMacOS)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "LinkBridge", "settings.json");
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "LinkBridge", "settings.json");
    }
}

