using System.Runtime.InteropServices;
using System.Text.Json;

namespace LinkBridge.Media.Audio;

/// <summary>A reversible default-output lease. Caller must prove paired TLS and first PCM before Start.</summary>
public sealed class MacSharedSpeakerRoute : IDisposable
{
    public const string BlackHoleUid = "BlackHole2ch_UID";
    private readonly object _gate = new();
    private readonly IMacSharedSpeakerBackend _backend;
    private readonly string _ledgerPath;
    private readonly FileStream _fileLock;
    private RouteLedger? _lease;
    private bool _recoveringLease;
    private bool _disposed;
    public bool IsActive { get { lock (_gate) return _lease is not null; } }
    public static bool IsBackendAvailable()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try { return new CoreAudioSharedSpeakerBackend().GetOutputChannelCount(BlackHoleUid) == 2; }
        catch { return false; }
    }
    /// <summary>Native query failures remain errors, never invented ownership.</summary>
    public bool IsCurrentRoute { get { lock (_gate) return !_disposed && _lease is not null && _backend.GetDefaultOutputUid() == BlackHoleUid; } }

    public MacSharedSpeakerRoute() : this(new CoreAudioSharedSpeakerBackend(), Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support",
        "LinkBridge", "shared-speaker-route.json")) { }

    /// <summary>Injection is for isolated tests; this does not install a driver or touch input/system-sound defaults.</summary>
    public MacSharedSpeakerRoute(IMacSharedSpeakerBackend backend, string ledgerPath)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _ledgerPath = Path.GetFullPath(ledgerPath);
        var directory = Path.GetDirectoryName(_ledgerPath)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _fileLock = new FileStream(_ledgerPath + ".lock", PrivateFileOptions(FileMode.OpenOrCreate, FileAccess.ReadWrite));
    }

    /// <summary>Call once at startup before a new lease. Never routes into BlackHole; only returns an owned outstanding lease.</summary>
    public bool RecoverPendingLease()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lease is not null)
            {
                if (!_recoveringLease) throw new InvalidOperationException("共享音响路由正在使用，不能执行崩溃恢复。");
                var retried = ReturnLease(); _recoveringLease = false; return retried;
            }
            if (!File.Exists(_ledgerPath)) return false;
            var info = new FileInfo(_ledgerPath);
            if (info.Length > 8192 || info.LinkTarget is not null) throw new InvalidDataException("音响路由恢复记录无效。");
            var record = JsonSerializer.Deserialize<RouteLedger>(File.ReadAllText(_ledgerPath))
                ?? throw new InvalidDataException("音响路由恢复记录为空。");
            Validate(record);
            _lease = record;
            _recoveringLease = true;
            var restored = ReturnLease(); _recoveringLease = false; return restored;
        }
    }

    public void Start(bool pairedTlsConfirmed, bool firstPcmFrameConfirmed)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!pairedTlsConfirmed || !firstPcmFrameConfirmed)
                throw new InvalidOperationException("只有已配对 TLS 音频连接和首帧都确认后才能切换 Mac 输出。");
            if (_lease is not null)
            {
                if (_backend.GetDefaultOutputUid() == BlackHoleUid) return;
                ClearLedger(); _lease = null;
                throw new InvalidOperationException("用户已手动切换 Mac 输出，联桥不会在当前租约内重新切换。");
            }
            if (File.Exists(_ledgerPath)) throw new InvalidOperationException("存在未归还的音响路由记录，请先执行恢复。");
            RequireOutput(BlackHoleUid, exactChannels: 2);
            var original = _backend.GetDefaultOutputUid();
            if (string.IsNullOrWhiteSpace(original) || original == BlackHoleUid)
                throw new InvalidOperationException("当前输出已是 BlackHole 2ch，无法证明归属或保存实体原输出；未创建路由租约。");
            RequireOutput(original);
            var record = new RouteLedger(1, "com.linkbridge.desktop", Guid.NewGuid().ToString("N"), original, BlackHoleUid);
            WriteLedger(record); // write-ahead: crash between set and confirmation is recoverable
            _lease = record;
            // Recheck after disk I/O: a manual change during acquisition must win.
            if (_backend.GetDefaultOutputUid() != original)
            {
                ClearLedger(); _lease = null;
                throw new InvalidOperationException("Mac 输出在路由准备期间被修改，已取消切换。");
            }
            try
            {
                _backend.SetDefaultOutputUid(BlackHoleUid);
                if (_backend.GetDefaultOutputUid() != BlackHoleUid)
                    throw new InvalidOperationException("CoreAudio 未确认共享输出切换，或用户已更换输出。");
            }
            catch (Exception changeError)
            {
                try { ReturnLease(); }
                catch (Exception restoreError) { throw new AggregateException("共享音响切换失败，恢复也失败；保留路由记录以便重试。", changeError, restoreError); }
                throw;
            }
        }
    }

    /// <returns>True only when a previous output was actually restored.</returns>
    public bool Stop()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return ReturnLease(); }
    }

    private bool ReturnLease()
    {
        var lease = _lease;
        if (lease is null) return false;
        // Never overwrite a user's subsequent output selection, even if original output disappeared.
        if (_backend.GetDefaultOutputUid() != lease.RouteUid)
        {
            ClearLedger(); _lease = null; return false;
        }
        RequireOutput(lease.RouteUid, exactChannels: 2);
        RequireOutput(lease.OriginalUid); // missing original must not select a different device
        if (_backend.GetDefaultOutputUid() != lease.RouteUid)
        {
            ClearLedger(); _lease = null; return false;
        }
        _backend.SetDefaultOutputUid(lease.OriginalUid);
        if (_backend.GetDefaultOutputUid() != lease.OriginalUid)
            throw new InvalidOperationException("CoreAudio 未确认原输出恢复，路由恢复记录已保留。");
        ClearLedger(); _lease = null; return true;
    }

    private void RequireOutput(string uid, int? exactChannels = null)
    {
        var channels = _backend.GetOutputChannelCount(uid);
        if (channels <= 0 || (exactChannels.HasValue && channels != exactChannels))
            throw new InvalidOperationException($"Mac 音频输出已断开或通道配置不匹配：{uid}（输出通道 {channels}）。");
    }
    private static void Validate(RouteLedger record)
    {
        if (record.Version != 1 || record.Owner != "com.linkbridge.desktop" || record.RouteUid != BlackHoleUid
            || !Guid.TryParseExact(record.LeaseId, "N", out _) || string.IsNullOrWhiteSpace(record.OriginalUid)
            || record.OriginalUid == record.RouteUid || record.OriginalUid.Length > 2048)
            throw new InvalidDataException("音响路由恢复记录归属或 UID 无效，未更改默认设备。");
    }
    private void WriteLedger(RouteLedger record)
    {
        var temporary = _ledgerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, PrivateFileOptions(FileMode.CreateNew, FileAccess.Write)))
            {
                JsonSerializer.Serialize(file, record); file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _ledgerPath, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static FileStreamOptions PrivateFileOptions(FileMode mode, FileAccess access)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = FileShare.None };
        if (OperatingSystem.IsMacOS()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
    private void ClearLedger() { if (File.Exists(_ledgerPath)) File.Delete(_ledgerPath); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { ReturnLease(); }
            finally { _disposed = true; _fileLock.Dispose(); }
        }
    }
    private sealed record RouteLedger(int Version, string Owner, string LeaseId, string OriginalUid, string RouteUid);
}

public interface IMacSharedSpeakerBackend
{
    string GetDefaultOutputUid();
    int GetOutputChannelCount(string uid);
    void SetDefaultOutputUid(string uid);
}

internal sealed class CoreAudioSharedSpeakerBackend : IMacSharedSpeakerBackend
{
    public CoreAudioSharedSpeakerBackend() { EnsureMac(); }
    private const string Audio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string Cf = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Global = 0x676c6f62, Output = 0x6f757470;
    [StructLayout(LayoutKind.Sequential)] private readonly struct Address(uint selector, uint scope = Global)
    { public readonly uint Selector = selector, Scope = scope, Element = 0; }
    public string GetDefaultOutputUid()
    {
        EnsureMac(); var address = new Address(0x644f7574); uint size = 4;
        Check(GetUInt(1, in address, 0, IntPtr.Zero, ref size, out var id), "读取默认输出");
        if (size != 4 || id == 0) throw new InvalidDataException("CoreAudio 默认输出设备无效。");
        return ReadUid(id);
    }
    public int GetOutputChannelCount(string uid)
    {
        EnsureMac(); var id = Resolve(uid); if (id == 0) return 0;
        var address = new Address(0x736c6179, Output); // stream configuration, output scope only
        Check(AudioObjectGetPropertyDataSize(id, in address, 0, IntPtr.Zero, out var size), "读取输出通道尺寸");
        if (size < 8 || size > 65536) throw new InvalidDataException("CoreAudio 输出通道尺寸无效。");
        var data = Marshal.AllocHGlobal((int)size);
        try
        {
            Check(AudioObjectGetPropertyData(id, in address, 0, IntPtr.Zero, ref size, data), "读取输出通道");
            var buffers = Marshal.ReadInt32(data); if (buffers < 0 || 8L + buffers * 16L > size) throw new InvalidDataException("CoreAudio 输出通道布局无效。");
            var count = 0; for (var i = 0; i < buffers; i++) count = checked(count + Marshal.ReadInt32(data, 8 + i * 16)); return count;
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    public void SetDefaultOutputUid(string uid)
    {
        EnsureMac(); var id = Resolve(uid);
        if (id == 0 || GetOutputChannelCount(uid) <= 0) throw new InvalidOperationException("指定 CoreAudio 输出 UID 已断开。");
        var address = new Address(0x644f7574); // dOut, never dSys or dIn
        Check(SetUInt(1, in address, 0, IntPtr.Zero, 4, in id), "设置默认输出");
    }
    private static uint Resolve(string uid)
    {
        var address = new Address(0x64657623); Check(AudioObjectGetPropertyDataSize(1, in address, 0, IntPtr.Zero, out var size), "读取设备列表尺寸");
        if (size > 65536 || size % 4 != 0) throw new InvalidDataException("CoreAudio 设备列表尺寸无效。");
        var data = Marshal.AllocHGlobal((int)size);
        try
        {
            Check(AudioObjectGetPropertyData(1, in address, 0, IntPtr.Zero, ref size, data), "读取设备列表");
            uint found = 0;
            for (var i = 0; i < size / 4; i++) { var id = unchecked((uint)Marshal.ReadInt32(data, i * 4)); if (ReadUid(id) != uid) continue; if (found != 0) throw new InvalidDataException("CoreAudio UID 不唯一。"); found = id; }
            return found;
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    private static string ReadUid(uint id)
    {
        var address = new Address(0x75696420); uint size = (uint)IntPtr.Size;
        Check(GetString(id, in address, 0, IntPtr.Zero, ref size, out var text), "读取设备 UID");
        var data = IntPtr.Zero;
        try
        {
            if (size != IntPtr.Size || text == IntPtr.Zero) throw new InvalidDataException("CoreAudio UID 布局无效。");
            data = Marshal.AllocHGlobal(4096);
            if (!CFStringGetCString(text, data, 4096, 0x08000100)) throw new InvalidDataException("CoreAudio UID 编码无效。");
            return Marshal.PtrToStringUTF8(data) ?? throw new InvalidDataException("CoreAudio UID 为空。");
        }
        finally { if (data != IntPtr.Zero) Marshal.FreeHGlobal(data); if (text != IntPtr.Zero) CFRelease(text); }
    }
    private static void EnsureMac() { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("默认输出路由仅支持 macOS。"); }
    private static void Check(int status, string operation) { if (status != 0) throw new InvalidOperationException($"CoreAudio {operation}失败 OSStatus={status}。"); }
    [DllImport(Audio)] private static extern int AudioObjectGetPropertyDataSize(uint id, in Address address, uint qualifierSize, IntPtr qualifier, out uint size);
    [DllImport(Audio)] private static extern int AudioObjectGetPropertyData(uint id, in Address address, uint qualifierSize, IntPtr qualifier, ref uint size, IntPtr data);
    [DllImport(Audio, EntryPoint = "AudioObjectGetPropertyData")] private static extern int GetUInt(uint id, in Address address, uint qualifierSize, IntPtr qualifier, ref uint size, out uint data);
    [DllImport(Audio, EntryPoint = "AudioObjectGetPropertyData")] private static extern int GetString(uint id, in Address address, uint qualifierSize, IntPtr qualifier, ref uint size, out IntPtr data);
    [DllImport(Audio, EntryPoint = "AudioObjectSetPropertyData")] private static extern int SetUInt(uint id, in Address address, uint qualifierSize, IntPtr qualifier, uint size, in uint data);
    [DllImport(Cf)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr text, IntPtr buffer, nint size, uint encoding);
    [DllImport(Cf)] private static extern void CFRelease(IntPtr value);
}
