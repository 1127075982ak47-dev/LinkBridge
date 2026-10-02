using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LinkBridge.Core.Models;

namespace LinkBridge.Media;

/// <summary>
/// macOS 的 CoreAudio（AudioObject API）端点枚举。
///
/// 为什么必须单独写（坑 1）：AVFoundation 和 DirectShow 一样只枚举「输入」设备，
/// 音响这类「输出端点」它们根本看不到，只能走系统音频接口。
/// 这里用 P/Invoke 直调 CoreAudio 的 AudioObjectGetPropertyData*，
/// 对应 <c>mac-coreaudio-probe/coreaudio_probe.py</c> 里已经验证过的那套选择子。
/// </summary>
[SupportedOSPlatform("macos")]
internal static class CoreAudioEndpoints
{
    private const string CoreAudioFramework = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>kAudioObjectSystemObject：全局对象，用来提问「系统里有哪些音频设备」。</summary>
    private const uint SystemObject = 1;

    private const uint ScopeGlobal = 0x676C6F62; // 'glob'
    private const uint ScopeOutput = 0x6F757470; // 'outp'
    private const uint ElementMain = 0;

    private const uint SelectorDevices = 0x64657623;       // 'dev#'
    private const uint SelectorName = 0x6C6E616D;          // 'lnam'
    private const uint SelectorUid = 0x75696420;           // 'uid '
    private const uint SelectorStreamConfig = 0x736C6179;  // 'slay'
    private const uint SelectorVolumeScalar = 0x766F6C6D;  // 'volm'

    /// <summary>kCFStringEncodingUTF8。</summary>
    private const uint EncodingUtf8 = 0x08000100;

    // AudioBufferList 的内存布局：UInt32 mNumberBuffers + 4 字节对齐填充，
    // 之后每个 AudioBuffer（UInt32 channels / UInt32 byteSize / void* data）占 16 字节。
    // 这个 8 字节头部是实测出来的（设备只有一路时 AudioObjectGetPropertyDataSize 返回 24 = 8 + 16）。
    private const int AudioBufferListHeaderSize = 8;
    private const int AudioBufferSize = 16;

    private const int CfStringBufferSize = 4096;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct AudioObjectPropertyAddress
    {
        public readonly uint Selector;
        public readonly uint Scope;
        public readonly uint Element;

        public AudioObjectPropertyAddress(uint selector, uint scope, uint element)
        {
            Selector = selector;
            Scope = scope;
            Element = element;
        }
    }

    [DllImport(CoreAudioFramework)]
    private static extern int AudioObjectGetPropertyDataSize(
        uint inObjectID,
        in AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        out uint outDataSize);

    [DllImport(CoreAudioFramework)]
    private static extern int AudioObjectGetPropertyData(
        uint inObjectID,
        in AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        IntPtr outData);

    [DllImport(CoreAudioFramework)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AudioObjectHasProperty(uint objectId, in AudioObjectPropertyAddress address);

    [DllImport(CoreAudioFramework)]
    private static extern int AudioObjectIsPropertySettable(uint objectId,
        in AudioObjectPropertyAddress address, out byte isSettable);

    [DllImport(CoreAudioFramework, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int GetVolumeScalar(uint objectId, in AudioObjectPropertyAddress address,
        uint qualifierSize, IntPtr qualifier, ref uint size, out float scalar);

    [DllImport(CoreAudioFramework, EntryPoint = "AudioObjectSetPropertyData")]
    private static extern int SetVolumeScalar(uint objectId, in AudioObjectPropertyAddress address,
        uint qualifierSize, IntPtr qualifier, uint size, in float scalar);

    [DllImport(CoreFoundationFramework)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr theString, IntPtr buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr value);

    /// <summary>列出本机全部「音响」端点：Id 用稳定的端点 UID，不用会随系统语言变的显示名。</summary>
    public static IReadOnlyList<MediaDevice> EnumerateSpeakers(out string? error)
    {
        error = null;
        var result = new List<MediaDevice>();

        try
        {
            var defaultDevice = DefaultOutputDeviceId();
            foreach (var deviceId in AllDeviceIds())
            {
                if (OutputChannelCount(deviceId) == 0)
                {
                    continue;
                }

                var name = ReadString(deviceId, SelectorName);
                var uid = ReadString(deviceId, SelectorUid);

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uid))
                {
                    continue;
                }

                // BlackHole 这类虚拟回环：它不是真音响，共享它只会得到回环或静音。
                if (VirtualAudioDevices.IsLoopback(name, uid))
                {
                    continue;
                }

                result.Add(new MediaDevice(MediaDeviceKind.Speaker, uid, name, MediaBackend.CoreAudio)
                { IsDefaultPlayback = deviceId == defaultDevice });
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return result;
    }

    /// <summary>Reads the selected physical output's real scalar volume. No volume property means false, never an invented value.</summary>
    public static bool TryReadVolume(string uid, out double percent)
    {
        percent = 0;
        var device = ResolveOutput(uid);
        var elements = VolumeElements(device);
        if (elements.Count == 0) return false;
        double total = 0;
        foreach (var element in elements) total += ReadScalar(device, element);
        percent = total / elements.Count * 100;
        return true;
    }

    public static double? ReadVolume(string uid) => TryReadVolume(uid, out var percent) ? percent : null;

    /// <summary>Sets a supported master or both supported output channels, without changing mute or the default device.</summary>
    public static void WriteVolume(string uid, double percent)
    {
        if (!double.IsFinite(percent)) throw new ArgumentOutOfRangeException(nameof(percent));
        var device = ResolveOutput(uid);
        var writable = new List<(uint Element, float Previous)>();
        foreach (var element in VolumeElements(device, true))
            writable.Add((element, ReadScalar(device, element)));
        if (writable.Count == 0)
            throw new NotSupportedException("所选 Mac 音频输出没有可写系统音量；请使用设备自身的音量控制。");
        var scalar = (float)(Math.Clamp(percent, 0, 100) / 100);
        var changed = 0;
        try
        {
            foreach (var channel in writable)
            {
                var address = new AudioObjectPropertyAddress(SelectorVolumeScalar, ScopeOutput, channel.Element);
                CheckStatus(SetVolumeScalar(device, in address, 0, IntPtr.Zero, sizeof(float), in scalar),
                    "写入音量", device, channel.Element);
                changed++;
            }
        }
        catch
        {
            // Preserve the previous channel balance if a later channel rejects the write.
            for (var index = 0; index < changed; index++)
            {
                var previous = writable[index].Previous;
                var address = new AudioObjectPropertyAddress(SelectorVolumeScalar, ScopeOutput, writable[index].Element);
                SetVolumeScalar(device, in address, 0, IntPtr.Zero, sizeof(float), in previous);
            }
            throw;
        }
    }

    public static bool CanWriteVolume(string uid) => VolumeElements(ResolveOutput(uid), true).Count != 0;

    /// <summary>Resolve empty selection to the actual dOut UID; an explicit missing/input-only UID never falls back.</summary>
    internal static string ResolveOutputUid(string uid)
    {
        ArgumentNullException.ThrowIfNull(uid);
        var resolved = ReadString(ResolveOutput(uid), SelectorUid);
        return !string.IsNullOrWhiteSpace(resolved) ? resolved
            : throw new InvalidOperationException("无法读取所选 Mac 音频输出的 UID；未选择其他输出。");
    }
    private static uint ResolveOutput(string uid)
    {
        var device = string.IsNullOrEmpty(uid) ? DefaultOutputDeviceId()
            : AllDeviceIds().FirstOrDefault(id => string.Equals(ReadString(id, SelectorUid), uid, StringComparison.Ordinal));
        if (device == 0 || OutputChannelCount(device) == 0)
            throw new InvalidOperationException("所选 Mac 音频输出已断开或不存在。");
        return device;
    }

    private static List<uint> VolumeElements(uint device, bool requireWritable = false)
    {
        bool Supported(uint element)
        {
            var address = new AudioObjectPropertyAddress(SelectorVolumeScalar, ScopeOutput, element);
            if (!AudioObjectHasProperty(device, in address)) return false;
            if (!requireWritable) return true;
            CheckStatus(AudioObjectIsPropertySettable(device, in address, out var settable),
                "查询音量可写状态", device, element);
            return settable != 0;
        }
        if (Supported(ElementMain)) return [ElementMain];
        var result = new List<uint>(2);
        for (uint element = 1; element <= Math.Min(2, OutputChannelCount(device)); element++)
            if (Supported(element)) result.Add(element);
        return result;
    }
    private static float ReadScalar(uint device, uint element)
    {
        var address = new AudioObjectPropertyAddress(SelectorVolumeScalar, ScopeOutput, element);
        uint size = sizeof(float);
        CheckStatus(GetVolumeScalar(device, in address, 0, IntPtr.Zero, ref size, out var scalar),
            "读取音量", device, element);
        if (size != sizeof(float) || !float.IsFinite(scalar) || scalar is < 0 or > 1)
            throw new InvalidOperationException("CoreAudio 返回了无效的音量标量。");
        return scalar;
    }

    private static void CheckStatus(int status, string operation, uint device, uint element)
    {
        if (status == 0) return;
        var code = (uint)status;
        var fourCC = new string([(char)((code >> 24) & 255), (char)((code >> 16) & 255),
            (char)((code >> 8) & 255), (char)(code & 255)]);
        var printable = fourCC.All(character => character is >= ' ' and <= '~') ? $" ('{fourCC}')" : "";
        throw new ExternalException($"CoreAudio {operation}失败：OSStatus {status}{printable}，设备 {device}，声道 {element}。", status);
    }

    private static uint DefaultOutputDeviceId()
    {
        var address = new AudioObjectPropertyAddress(0x644F7574, ScopeGlobal, ElementMain); // 'dOut'
        uint size = sizeof(uint);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return AudioObjectGetPropertyData(SystemObject, in address, 0, IntPtr.Zero, ref size, buffer) == 0
                ? (uint)Marshal.ReadInt32(buffer) : 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>系统对象上的 'dev#'：本机全部音频设备 ID。</summary>
    private static uint[] AllDeviceIds()
    {
        var address = new AudioObjectPropertyAddress(SelectorDevices, ScopeGlobal, ElementMain);

        if (AudioObjectGetPropertyDataSize(SystemObject, in address, 0, IntPtr.Zero, out var size) != 0
            || size < sizeof(uint))
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(SystemObject, in address, 0, IntPtr.Zero, ref size, buffer) != 0)
            {
                return [];
            }

            var count = (int)(size / sizeof(uint));
            var ids = new uint[count];
            for (var i = 0; i < count; i++)
            {
                ids[i] = (uint)Marshal.ReadInt32(buffer, i * sizeof(uint));
            }

            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>该设备在「输出」方向上开了几路通道；0 就是纯输入设备。</summary>
    private static int OutputChannelCount(uint deviceId)
    {
        var address = new AudioObjectPropertyAddress(SelectorStreamConfig, ScopeOutput, ElementMain);

        if (AudioObjectGetPropertyDataSize(deviceId, in address, 0, IntPtr.Zero, out var size) != 0
            || size < AudioBufferListHeaderSize)
        {
            return 0;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(deviceId, in address, 0, IntPtr.Zero, ref size, buffer) != 0
                || size < AudioBufferListHeaderSize)
            {
                return 0;
            }

            var bufferCount = Marshal.ReadInt32(buffer);
            var total = 0;

            for (var i = 0; i < bufferCount; i++)
            {
                var offset = AudioBufferListHeaderSize + (i * AudioBufferSize);
                if (offset + AudioBufferSize > size)
                {
                    break;
                }

                total += Marshal.ReadInt32(buffer, offset);
            }

            return total;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>读一个返回 CFStringRef 的属性（'lnam' / 'uid '）。</summary>
    private static string? ReadString(uint objectId, uint selector)
    {
        var address = new AudioObjectPropertyAddress(selector, ScopeGlobal, ElementMain);

        if (AudioObjectGetPropertyDataSize(objectId, in address, 0, IntPtr.Zero, out var size) != 0
            || size < IntPtr.Size)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(objectId, in address, 0, IntPtr.Zero, ref size, buffer) != 0)
            {
                return null;
            }

            var cfString = Marshal.ReadIntPtr(buffer);
            if (cfString == IntPtr.Zero)
            {
                return null;
            }

            // AudioHardwareBase.h explicitly assigns ownership to the caller for
            // kAudioObjectPropertyName and kAudioDevicePropertyDeviceUID. Copy the
            // string, then release on both conversion success and failure.
            try { return ToManagedString(cfString); }
            finally { CFRelease(cfString); }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ToManagedString(IntPtr cfString)
    {
        var buffer = Marshal.AllocHGlobal(CfStringBufferSize);
        try
        {
            return CFStringGetCString(cfString, buffer, CfStringBufferSize, EncodingUtf8)
                ? Marshal.PtrToStringUTF8(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

