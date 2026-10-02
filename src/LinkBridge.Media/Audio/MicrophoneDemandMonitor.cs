using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LinkBridge.Media;

public enum MicrophoneDemandStatus { Known, MissingEndpoint, RejectedEndpoint, Unsupported, Unknown, Stopped }

/// <summary>Unknown/missing is null demand, never an invented zero. PIDs are owner processes.</summary>
public sealed record MicrophoneDemandSnapshot(MicrophoneDemandStatus Status, int? ActiveConsumers,
    IReadOnlyList<int> ProcessIds, string Detail);

/// <summary>
/// Read-only virtual capture endpoint demand. Polls at most once per second on a worker,
/// opens no audio client/capture stream, and excludes this process. A process using more
/// than one Mac input device or a shared Windows session is explicitly ambiguous.
/// </summary>
public sealed class MicrophoneDemandMonitor : IDisposable
{
    private readonly Action<MicrophoneDemandSnapshot> _callback;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _disposed;
    private int _callbackThread;
    private MicrophoneDemandSnapshot _last = Result(MicrophoneDemandStatus.Stopped, "需求监视尚未启动。");
    public MicrophoneDemandSnapshot LastObservation => Volatile.Read(ref _last);

    public MicrophoneDemandMonitor(Action<MicrophoneDemandSnapshot> callback) =>
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));

    public void Start(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        Stop();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var source = new CancellationTokenSource();
            _cts = source;
            _task = Task.Run(() => RunAsync(endpointId, source));
        }
    }

    public void Stop()
    {
        Task? oldTask;
        CancellationTokenSource? oldSource;
        bool inCallback;
        lock (_gate)
        {
            oldSource = _cts; oldTask = _task;
            _cts = null; _task = null;
            oldSource?.Cancel();
            inCallback = _callbackThread == Environment.CurrentManagedThreadId;
            Volatile.Write(ref _last, Result(MicrophoneDemandStatus.Stopped, "需求监视已停止。"));
        }
        // A callback is allowed to stop its own monitor without joining itself.
        if (!inCallback) oldTask?.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        Stop();
    }

    private async Task RunAsync(string endpointId, CancellationTokenSource source)
    {
        using (source)
        using (var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)))
        {
            try
            {
                while (!source.IsCancellationRequested)
                {
                    MicrophoneDemandSnapshot observation;
                    try
                    {
                        observation = OperatingSystem.IsWindows() ? ReadWindows(endpointId)
                            : OperatingSystem.IsMacOS() ? ReadMac(endpointId)
                            : Result(MicrophoneDemandStatus.Unsupported, "此平台没有可验证的虚拟麦克风需求接口。");
                    }
                    catch (Exception ex)
                    {
                        observation = Result(MicrophoneDemandStatus.Unknown,
                            $"麦克风需求元数据查询失败：{ex.GetBaseException().Message}");
                    }
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_cts, source) || source.IsCancellationRequested || _disposed) return;
                        if (!Equivalent(_last, observation))
                        {
                            Volatile.Write(ref _last, observation);
                            _callbackThread = Environment.CurrentManagedThreadId;
                            try { _callback(observation); }
                            catch (Exception ex)
                            {
                                if (ReferenceEquals(_cts, source) && !_disposed)
                                    Volatile.Write(ref _last, Result(MicrophoneDemandStatus.Unknown,
                                        "需求回调失败：" + ex.Message));
                            }
                            finally { _callbackThread = 0; }
                        }
                    }
                    if (!await timer.WaitForNextTickAsync(source.Token).ConfigureAwait(false)) return;
                }
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_cts, source)) { _cts = null; _task = null; }
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static MicrophoneDemandSnapshot ReadWindows(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice endpoint;
        try { endpoint = enumerator.GetDevice(id); }
        catch (COMException ex) when (ex.HResult is unchecked((int)0x80070490) or unchecked((int)0x80070057))
        { return Result(MicrophoneDemandStatus.MissingEndpoint, "指定的虚拟应用输入端尚未安装或已消失。"); }
        catch (ArgumentException)
        { return Result(MicrophoneDemandStatus.MissingEndpoint, "指定的虚拟应用输入端 ID 不存在或无效。"); }
        using (endpoint)
        {
            if (endpoint.DataFlow != DataFlow.Capture || !VirtualAudioDevices.IsLoopback(endpoint.FriendlyName, endpoint.ID))
                return Result(MicrophoneDemandStatus.RejectedEndpoint, "只监视已安装的虚拟 Capture 端，拒绝实体麦克风或 Render 端。");
            if (endpoint.State != DeviceState.Active)
                return Result(MicrophoneDemandStatus.MissingEndpoint, "虚拟应用输入端当前不可用。");
            var manager = endpoint.AudioSessionManager;
            try
            {
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                if (sessions is null) return Result(MicrophoneDemandStatus.Unsupported, "系统未提供该 Capture 端的会话列表。");
                var owners = new HashSet<int>();
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                    var pid = ReadUnambiguousSessionPid(session);
                    if (pid is null || pid == 0)
                        return Result(MicrophoneDemandStatus.Unknown, "活动录制会话无法唯一归属应用进程，不能据此推断需求为零。");
                    if (pid != Environment.ProcessId) owners.Add(pid.Value);
                }
                return Known(owners, "Windows Capture 会话元数据；排除联桥自身，只计算明确的活动应用进程。");
            }
            finally { manager.Dispose(); }
        }
    }

    // NAudio 2.2.1's public GetProcessID discards AUDCLNT_S_NO_SINGLE_PROCESS.
    // Inspect its pinned interface directly to preserve that success-warning. If the
    // dependency changes, fail Unknown instead of accepting a guessed owner process.
    private static int? ReadUnambiguousSessionPid(AudioSessionControl session)
    {
        var field = typeof(AudioSessionControl).GetField("audioSessionControlInterface2", BindingFlags.Instance | BindingFlags.NonPublic);
        var native = field?.GetValue(session);
        var method = field?.FieldType.GetMethod("GetProcessId");
        if (native is null || method is null) return null;
        object?[] arguments = [0u];
        var result = method.Invoke(native, arguments);
        if (result is not int hr) return null;
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        if (hr != 0) return null; // includes AUDCLNT_S_NO_SINGLE_PROCESS
        return arguments[0] is uint pid && pid <= int.MaxValue ? (int)pid : null;
    }

    [SupportedOSPlatform("macos")]
    private static MicrophoneDemandSnapshot ReadMac(string uid)
    {
        uint target = 0;
        foreach (var device in Ids(1, Four("dev#"), Global))
        {
            if (CfText(device, Four("uid ")) != uid) continue;
            var name = CfText(device, Four("lnam"));
            if (!VirtualAudioDevices.IsLoopback(name, uid))
                return Result(MicrophoneDemandStatus.RejectedEndpoint, "只监视已安装的虚拟 CoreAudio 输入端，拒绝实体麦克风。");
            if (Ids(device, Four("stm#"), Input).Length == 0)
                return Result(MicrophoneDemandStatus.RejectedEndpoint, "指定虚拟设备没有应用可读取的输入流。");
            target = device; break;
        }
        if (target == 0) return Result(MicrophoneDemandStatus.MissingEndpoint, "指定的虚拟应用输入 UID 尚未安装或已消失。");
        var listAddress = new Address(Four("prs#"), Global);
        if (!AudioObjectHasProperty(1, in listAddress))
            return Result(MicrophoneDemandStatus.Unsupported, "当前 CoreAudio 不支持 ProcessObjectList，需求状态未知。");
        var owners = new HashSet<int>();
        foreach (var process in Ids(1, Four("prs#"), Global))
        {
            var pid = Scalar(process, Four("ppid"), Global);
            if (pid == Environment.ProcessId) continue;
            if (Scalar(process, Four("piri"), Global) == 0) continue;
            // SDK: pdv# scope selects INPUT or OUTPUT device list. Do not use glob:
            // an output-only AudioQueue into BlackHole is not an application reader.
            var inputs = Ids(process, Four("pdv#"), Input).Distinct().ToArray();
            if (!inputs.Contains(target)) continue;
            if (inputs.Length != 1 || pid == 0 || pid > int.MaxValue)
                return Result(MicrophoneDemandStatus.Unknown,
                    "CoreAudio 进程有多个输入设备或无明确 PID，不能确定该虚拟端是否正在被读。");
            owners.Add((int)pid);
        }
        return Known(owners, "CoreAudio piri + pdv# 输入 scope；排除自身和仅向虚拟设备输出的进程。");
    }

    private static MicrophoneDemandSnapshot Known(HashSet<int> pids, string detail)
    {
        var sorted = pids.Order().ToArray();
        return new(MicrophoneDemandStatus.Known, sorted.Length, sorted, detail);
    }
    private static MicrophoneDemandSnapshot Result(MicrophoneDemandStatus status, string detail) =>
        new(status, null, Array.Empty<int>(), detail);
    private static bool Equivalent(MicrophoneDemandSnapshot a, MicrophoneDemandSnapshot b) =>
        a.Status == b.Status && a.ActiveConsumers == b.ActiveConsumers && a.Detail == b.Detail && a.ProcessIds.SequenceEqual(b.ProcessIds);

    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Global = 0x676C6F62; // glob
    private const uint Input = 0x696E7074; // inpt
    private static uint Four(string value) => ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];
    [StructLayout(LayoutKind.Sequential)] private readonly struct Address(uint selector, uint scope)
    {
        public readonly uint Selector = selector, Scope = scope, Element = 0;
    }
    private static uint[] Ids(uint obj, uint selector, uint scope)
    {
        var address = new Address(selector, scope);
        Check(AudioObjectGetPropertyDataSize(obj, in address, 0, IntPtr.Zero, out var size), selector);
        if (size % 4 != 0 || size > 65536) throw new InvalidOperationException("Invalid CoreAudio object-array size.");
        if (size == 0) return [];
        var capacity = size;
        var data = Marshal.AllocHGlobal((int)size);
        try
        {
            Check(AudioObjectGetPropertyData(obj, in address, 0, IntPtr.Zero, ref size, data), selector);
            if (size > capacity || size % 4 != 0)
                throw new InvalidOperationException("CoreAudio object-array size changed during query.");
            var result = new int[size / 4]; Marshal.Copy(data, result, 0, result.Length);
            return result.Select(x => unchecked((uint)x)).ToArray();
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    private static uint Scalar(uint obj, uint selector, uint scope)
    {
        var address = new Address(selector, scope); uint size = 4;
        Check(GetScalar(obj, in address, 0, IntPtr.Zero, ref size, out var value), selector);
        if (size != 4) throw new InvalidOperationException("Invalid CoreAudio scalar size.");
        return value;
    }
    private static string CfText(uint obj, uint selector)
    {
        var address = new Address(selector, Global); uint size = (uint)IntPtr.Size;
        Check(GetCfText(obj, in address, 0, IntPtr.Zero, ref size, out var value), selector);
        if (value == IntPtr.Zero) throw new InvalidOperationException("CoreAudio returned a null CFString.");
        try
        {
            var text = new byte[4096];
            if (!CFStringGetCString(value, text, text.Length, 0x08000100))
                throw new InvalidOperationException("CoreAudio CFString conversion failed.");
            var end = Array.IndexOf(text, (byte)0);
            return System.Text.Encoding.UTF8.GetString(text, 0, end < 0 ? text.Length : end);
        }
        finally { CFRelease(value); }
    }
    private static void Check(int status, uint selector)
    {
        if (status != 0) throw new InvalidOperationException($"CoreAudio selector 0x{selector:X8}: OSStatus {status} (0x{unchecked((uint)status):X8}).");
    }
    [DllImport(CoreAudio)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AudioObjectHasProperty(uint obj, in Address address);
    [DllImport(CoreAudio)] private static extern int AudioObjectGetPropertyDataSize(uint obj, in Address address, uint qualifierSize, IntPtr qualifier, out uint size);
    [DllImport(CoreAudio)] private static extern int AudioObjectGetPropertyData(uint obj, in Address address, uint qualifierSize, IntPtr qualifier, ref uint size, IntPtr data);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")] private static extern int GetScalar(uint obj, in Address address, uint qualifierSize, IntPtr qualifier, ref uint size, out uint data);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")] private static extern int GetCfText(uint obj, in Address address, uint qualifierSize, IntPtr qualifier, ref uint size, out IntPtr data);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr text, [Out] byte[] buffer, nint size, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
}
