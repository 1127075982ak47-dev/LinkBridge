using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LinkBridge.Media.Audio;

public sealed record SharedMicrophoneEndpoint(bool Available,string? Id,string? OutputName,string ApplicationInputName,string Diagnostic)
{
    /// <summary>Read-side endpoint selected by applications (Windows Capture ID / Mac BlackHole16ch UID).</summary>
    public string? ApplicationInputId { get; init; }
}

/// <summary>Independent microphone-only virtual route; never sets an OS default device.</summary>
public static class SharedMicrophoneEndpoints
{
    public static SharedMicrophoneEndpoint Diagnose()
    {
        try
        {
            if(OperatingSystem.IsWindows())
            {
                using var enumerator=new MMDeviceEnumerator();
                string? outputId=null,outputName=null,inputName=null,inputId=null;
                foreach(var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active))
                    using(endpoint)if(IsCableInput(endpoint.FriendlyName)){if(outputId is not null)throw new InvalidOperationException("多个CABLE Input，需先解决设备重名。");outputId=endpoint.ID;outputName=endpoint.FriendlyName;}
                foreach(var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active))
                    using(endpoint)if(IsCableOutput(endpoint.FriendlyName)){if(inputId is not null)throw new InvalidOperationException("多个CABLE Output，需先解决设备重名。");inputName=endpoint.FriendlyName;inputId=endpoint.ID;}
                return outputId is not null&&inputName is not null
                    ?new(true,outputId,outputName,inputName,"虚拟麦克风已就绪；应用输入请选择CABLE Output。") { ApplicationInputId=inputId }
                    :new(false,null,null,"CABLE Output","尚未安装或启用独立VB-CABLE虚拟麦克风后端；不会改用实体音响。安装驱动可能需要重启，请安排安装后再开启。");
            }
            if(OperatingSystem.IsMacOS())
            {
                var outputs=MacVirtualDevices().Where(d=>d.Name.Equals("BlackHole 16ch",StringComparison.OrdinalIgnoreCase)&&d.OutputChannels==16&&d.InputChannels==16).ToArray();
                if(outputs.Length>1)throw new InvalidOperationException("多个BlackHole16ch，需先解决设备重名。");
                return outputs.Length==1?new(true,outputs[0].Uid,outputs[0].Name,"BlackHole 16ch","虚拟麦克风独立使用BlackHole16ch；音乐继续使用BlackHole2ch。") { ApplicationInputId=outputs[0].Uid }
                    :new(false,null,null,"BlackHole 16ch","未发现独立BlackHole16ch虚拟麦克风；现有BlackHole2ch保留音乐用途，不能复用以免音乐混入麦克风。");
            }
            return new(false,null,null,"共享麦克风","当前平台没有受支持的虚拟麦克风后端。");
        }
        catch(Exception ex){return new(false,null,null,OperatingSystem.IsWindows()?"CABLE Output":"BlackHole 16ch","虚拟麦克风设备检查失败："+ex.Message);}
    }
    public static ISharedMicrophoneSink CreateVirtualSink(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        var endpoint=Diagnose();
        if(!endpoint.Available||!string.Equals(endpoint.Id,endpointId,StringComparison.Ordinal))
            throw new InvalidOperationException(endpoint.Available?"所选输出不是当前专用虚拟麦克风端点。":endpoint.Diagnostic);
        return OperatingSystem.IsWindows()?new WindowsSink(endpointId):new MacSink(endpointId);
    }
    public static ISharedMicrophoneSink CreateVirtualSink()
    {
        var endpoint=Diagnose();if(!endpoint.Available)throw new InvalidOperationException(endpoint.Diagnostic);
        return CreateVirtualSink(endpoint.Id!);
    }
    private static bool IsCableInput(string name)=>name.StartsWith("CABLE Input (",StringComparison.OrdinalIgnoreCase)&&name.Contains("VB-Audio",StringComparison.OrdinalIgnoreCase);
    private static bool IsCableOutput(string name)=>name.StartsWith("CABLE Output (",StringComparison.OrdinalIgnoreCase)&&name.Contains("VB-Audio",StringComparison.OrdinalIgnoreCase);
    private sealed class WindowsSink : ISharedMicrophoneSink
    {
        private readonly object _gate=new();
        private readonly MMDevice _endpoint;
        private readonly WasapiOut _output;
        private readonly BufferedWaveProvider _buffer=new(new WaveFormat(48000,16,2)){BufferDuration=TimeSpan.FromMilliseconds(100),DiscardOnBufferOverflow=false};
        private bool _disposed;
        private Exception? _fault;
        public string EndpointName=>_endpoint.FriendlyName;
        public WindowsSink(string id)
        {
            using var enumerator=new MMDeviceEnumerator();_endpoint=enumerator.GetDevice(id);
            if(_endpoint.DataFlow!=DataFlow.Render||!IsCableInput(_endpoint.FriendlyName)){_endpoint.Dispose();throw new InvalidOperationException("麦克风只能写入指定CABLE Input，不能播放到默认音响。");}
            _output=new WasapiOut(_endpoint,AudioClientShareMode.Shared,false,40);
            try
            {
                _output.Init(_buffer);_output.PlaybackStopped+=(_,e)=>{if(e.Exception is not null)Volatile.Write(ref _fault,e.Exception);};_output.Play();
            }
            catch{_output.Dispose();_endpoint.Dispose();throw;}
        }
        public void Write(ReadOnlySpan<byte> pcm)
        {
            if(pcm.Length%4!=0)throw new ArgumentException("完整双声道PCM帧必须为4字节对齐。",nameof(pcm));
            lock(_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed,this);if(_fault is {} error)throw new IOException("虚拟麦克风输出停止。",error);
                if(_output.PlaybackState!=PlaybackState.Playing)throw new IOException("专用虚拟麦克风输出未运行。");
                if(pcm.Length>_buffer.BufferLength)pcm=pcm[^_buffer.BufferLength..];
                if(pcm.Length>_buffer.BufferLength-_buffer.BufferedBytes)_buffer.ClearBuffer();
                // One bounded 20ms network chunk; AddSamples copies immediately.
                var data=pcm.ToArray();_buffer.AddSamples(data,0,data.Length);
            }
        }
        public void Dispose(){lock(_gate){if(_disposed)return;_disposed=true;_buffer.ClearBuffer();try{_output.Stop();}finally{_output.Dispose();_endpoint.Dispose();}}}
    }
    private sealed class MacSink(string uid) : ISharedMicrophoneSink
    {
        private readonly MacPcmOutput _output=new(uid);
        public string EndpointName=>"BlackHole 16ch";
        public void Write(ReadOnlySpan<byte> pcm)=>_output.Write(pcm);
        public void Dispose()=>_output.Dispose();
    }
    private sealed record MacDevice(string Uid,string Name,int OutputChannels,int InputChannels);
    private const string Audio="/System/Library/Frameworks/CoreAudio.framework/CoreAudio",Cf="/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Address(uint Selector,uint Scope=0x676c6f62,uint Element=0);
    private static List<MacDevice> MacVirtualDevices()
    {
        var result=new List<MacDevice>();var address=new Address(0x64657623);
        Check(AudioObjectGetPropertyDataSize(1,in address,0,IntPtr.Zero,out var size));
        if(size>65536||size%4!=0)throw new InvalidDataException("CoreAudio设备列表尺寸无效。");
        var data=Marshal.AllocHGlobal((int)size);
        try
        {
            Check(AudioObjectGetPropertyData(1,in address,0,IntPtr.Zero,ref size,data));
            for(var offset=0;offset+4<=size;offset+=4)
            {
                var id=(uint)Marshal.ReadInt32(data,offset);var name=ReadString(id,0x6c6e616d);
                if(!name.Equals("BlackHole 16ch",StringComparison.OrdinalIgnoreCase))continue;
                result.Add(new(ReadString(id,0x75696420),name,Channels(id,0x6f757470),Channels(id,0x696e7074)));
            }
        }
        finally{Marshal.FreeHGlobal(data);}return result;
    }
    private static int Channels(uint id,uint scope)
    {
        var address=new Address(0x736c6179,scope);Check(AudioObjectGetPropertyDataSize(id,in address,0,IntPtr.Zero,out var size));
        if(size<8||size>65536)throw new InvalidDataException("CoreAudio通道列表尺寸无效。");
        var data=Marshal.AllocHGlobal((int)size);
        try
        {
            Check(AudioObjectGetPropertyData(id,in address,0,IntPtr.Zero,ref size,data));var buffers=Marshal.ReadInt32(data);var channels=0;
            if(buffers<0||8L+buffers*16L>size)throw new InvalidDataException("CoreAudio通道布局无效。");
            for(var i=0;i<buffers;i++)channels=checked(channels+Marshal.ReadInt32(data,8+i*16));return channels;
        }
        finally{Marshal.FreeHGlobal(data);}
    }
    private static string ReadString(uint id,uint selector)
    {
        var address=new Address(selector);uint size=(uint)IntPtr.Size;var data=Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Check(AudioObjectGetPropertyData(id,in address,0,IntPtr.Zero,ref size,data));if(size!=IntPtr.Size)throw new InvalidDataException("CoreAudio字符串布局无效。");
            var text=Marshal.ReadIntPtr(data);if(text==IntPtr.Zero)return string.Empty;var buffer=Marshal.AllocHGlobal(4096);
            try{return CFStringGetCString(text,buffer,4096,0x08000100)?Marshal.PtrToStringUTF8(buffer)??string.Empty:throw new InvalidDataException("CoreAudio字符串编码无效。");}
            finally{Marshal.FreeHGlobal(buffer);CFRelease(text);}
        }
        finally{Marshal.FreeHGlobal(data);}
    }
    private static void Check(int status){if(status!=0)throw new InvalidOperationException($"CoreAudio麦克风端点检查失败 OSStatus={status}。");}
    [DllImport(Audio)] private static extern int AudioObjectGetPropertyDataSize(uint id,in Address address,uint qualifierSize,IntPtr qualifier,out uint size);
    [DllImport(Audio)] private static extern int AudioObjectGetPropertyData(uint id,in Address address,uint qualifierSize,IntPtr qualifier,ref uint size,IntPtr data);
    [DllImport(Cf)] [return:MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr text,IntPtr buffer,nint size,uint encoding);
    [DllImport(Cf)] private static extern void CFRelease(IntPtr value);
}
