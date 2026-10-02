using System.Diagnostics;
using System.Globalization;
using LinkBridge.Core;
using LinkBridge.Core.Models;

namespace LinkBridge.Media.Audio;

internal sealed class SharedMicrophoneCapture : ISharedMicrophoneCapture
{
    private readonly Process _process;
    private int _disposed;
    public Stream PcmStream=>_process.StandardOutput.BaseStream;
    private SharedMicrophoneCapture(Process process)=>_process=process;
    public static async ValueTask<ISharedMicrophoneCapture> OpenAsync(MediaDevice source,Action<string> log,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if(source.Kind!=MediaDeviceKind.Microphone||source.IsApplicationEndpoint||IsVirtual(source.Name,source.Id))
            throw new ArgumentException("共享来源必须是实体麦克风；不能重采集虚拟麦克风回环。",nameof(source));
        var expected=OperatingSystem.IsWindows()?MediaBackend.DirectShow:OperatingSystem.IsMacOS()?MediaBackend.AvFoundation:throw new PlatformNotSupportedException();
        if(source.Backend!=expected)throw new ArgumentException("麦克风采集后端与当前平台不符。",nameof(source));
        // AVFoundation indices may change after a USB reconnect. Resolve a fresh exact
        // name match before capture; never fall back to the default microphone.
        var matches=(await new MediaDeviceCatalog().EnumerateAsync(ct).ConfigureAwait(false)).Where(d=>d.Kind==MediaDeviceKind.Microphone&&
            d.Backend==expected&&string.Equals(d.Name,source.Name,StringComparison.Ordinal)&&!d.IsApplicationEndpoint&&!IsVirtual(d.Name,d.Id)).ToArray();
        if(matches.Length!=1)throw new InvalidOperationException("所选实体麦克风已断开或名称不唯一；请重新选择麦克风。");
        var current=matches[0];
        if(expected==MediaBackend.AvFoundation&&!int.TryParse(current.Id,NumberStyles.None,CultureInfo.InvariantCulture,out _))
            throw new InvalidOperationException("AVFoundation麦克风索引无效。");
        var info=new ProcessStartInfo(FfmpegLocator.ExecutablePath??throw new InvalidOperationException("未找到ffmpeg。"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-hide_banner","-loglevel","warning","-nostdin","-f",expected==MediaBackend.DirectShow?"dshow":"avfoundation","-i",
            expected==MediaBackend.DirectShow?"audio="+current.Id:":"+current.Id,"-vn","-ac","2","-ar","48000","-c:a","pcm_s16le","-f","s16le","pipe:1"})info.ArgumentList.Add(arg);
        var process=new Process{StartInfo=info};process.ErrorDataReceived+=(_,e)=>{if(!string.IsNullOrWhiteSpace(e.Data))log("[麦克风] "+e.Data);};
        try{ct.ThrowIfCancellationRequested();if(!process.Start())throw new InvalidOperationException("麦克风采集启动失败。");process.BeginErrorReadLine();ct.ThrowIfCancellationRequested();return new SharedMicrophoneCapture(process);}
        catch{ProcessHelper.TryKill(process);process.Dispose();throw;}
    }
    internal static bool IsVirtual(string? name,string? id)=>VirtualAudioDevices.IsLoopback(name,id)||
        (name?.StartsWith("CABLE ",StringComparison.OrdinalIgnoreCase)??false)||
        (name?.Contains("联桥共享",StringComparison.OrdinalIgnoreCase)??false)||
        (name?.Contains("LinkBridge Shared",StringComparison.OrdinalIgnoreCase)??false);
    public void Stop(){if(Volatile.Read(ref _disposed)==0)ProcessHelper.TryKill(_process);}
    public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)!=0)return;ProcessHelper.TryKill(_process);_process.Dispose();}
}
