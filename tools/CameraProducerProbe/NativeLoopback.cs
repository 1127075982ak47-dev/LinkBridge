using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LinkBridge.Core;
using LinkBridge.Core.Models;
using LinkBridge.Media.Camera;

internal static class NativeLoopback
{
    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("指定 NVIDIA Broadcast 测试仅在 Windows 运行。");
        const string id = "Camera (NVIDIA Broadcast)";
        var evidence = Path.GetFullPath("docs/verification/2026-10-02/windows-camera-4k-loopback.log");
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        var messages = new List<string>(); var gate = new object();
        void Log(string line) { lock (gate) messages.Add(line); }
        Log("Controlled source: NVIDIA Broadcast virtual input only; no other camera opened.");
        Log("No images recorded, displayed or persisted; only metadata/progress. Destination is private loopback UDP.");
        var watch = Stopwatch.StartNew(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var decoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoderInfo = new ProcessStartInfo(FfmpegLocator.ExecutablePath ?? throw new InvalidOperationException("FFmpeg missing"))
        { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardError=true,RedirectStandardOutput=true };
        foreach(var arg in new[]{"-hide_banner","-loglevel","info","-nostats","-progress","pipe:1","-probesize","256k","-analyzeduration","500000","-f","mpegts","-i","pipe:0","-frames:v","1","-an","-f","null","-"})decoderInfo.ArgumentList.Add(arg);
        using var decoder=new Process { StartInfo=decoderInfo };
        await using var producer = new SharedCameraProducer();
        var sourcePid=0;var decoderPid=0;var sourceReady=false;var received4k=false;var input=false;var result=1;long bytes=0;
        producer.LogEmitted+=line=>Log("SOURCE "+line);
        Task? relay=null,metadata=null,progress=null;
        try
        {
            decoder.Start(); decoderPid=decoder.Id;
            metadata=Task.Run(async()=>
            {
                while(await decoder.StandardError.ReadLineAsync(deadline.Token) is { } line)
                {
                    Log("RECEIVER "+line);
                    if(line.StartsWith("Input #0",StringComparison.Ordinal))input=true;
                    else if(line.StartsWith("Output #",StringComparison.Ordinal))input=false;
                    if(input&&SharedCameraProducer.TryParseInputDimensions(line,out var w,out var h))
                    { if(w!=3840||h!=2160)throw new InvalidDataException($"Received input was {w}x{h}, not 4K.");received4k=true; }
                }
            });
            progress=Task.Run(async()=>
            {
                while(await decoder.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                    if(SharedCameraProducer.TryParseFrameProgress(line,out var frame))
                    { Log("RECEIVER decoded frame="+frame);if(received4k)decoded.TrySetResult(); }
            });
            relay=Task.Run(async()=>
            {
                while(!deadline.IsCancellationRequested)
                {
                    var packet=await udp.ReceiveAsync(deadline.Token);
                    if(!IPAddress.IsLoopback(packet.RemoteEndPoint.Address))throw new InvalidDataException("Unexpected non-loopback packet.");
                    Interlocked.Add(ref bytes,packet.Buffer.Length);
                    await decoder.StandardInput.BaseStream.WriteAsync(packet.Buffer,deadline.Token);
                }
            });
            var start=producer.StartAsync(new(MediaDeviceKind.Camera,id,id,MediaBackend.DirectShow),"127.0.0.1",port,deadline.Token);
            // Observe only this producer's owned Process; never enumerate or terminate unrelated apps.
            var session=typeof(SharedCameraProducer).GetField("_session",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(producer);
            if(session is not null)sourcePid=((Process)session.GetType().GetField("Process")!.GetValue(session)!).Id;
            sourceReady=await start.WaitAsync(TimeSpan.FromSeconds(5),deadline.Token);
            if(!sourceReady)throw new InvalidOperationException(producer.LastError??"Native source failed before readiness.");
            Log("SOURCE readiness confirmed: matching actual input metadata plus encoded first frame.");
            await decoded.Task.WaitAsync(TimeSpan.FromSeconds(2),deadline.Token);
            result=0;Log("PASS source actual 3840x2160 + h264_nvenc first frame and receiver actual 3840x2160 decoded frame.");
        }
        catch(Exception ex){Log("FAIL "+ex.GetType().Name+": "+ex.Message);}
        finally
        {
            // Stop native capture first, before waiting for decoder/relay cleanup.
            producer.ProducerStopSync();deadline.Cancel();udp.Dispose();ProcessHelper.TryKill(decoder);
            await producer.StopAsync();
            try{await decoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));}catch{}
            foreach(var task in new[]{relay,metadata,progress})if(task is not null)try{await task.WaitAsync(TimeSpan.FromSeconds(1));}catch{}
            foreach(var pid in new[]{sourcePid,decoderPid})if(pid!=0)
            {
                try{using var child=Process.GetProcessById(pid);if(!child.HasExited){result=1;Log("FAIL owned PID still running: "+pid);}else Log("Owned PID reaped: "+pid);}
                catch(ArgumentException){Log("Owned PID reaped: "+pid);}
            }
            Log($"Duration {watch.Elapsed.TotalSeconds:0.000}s; sourceReady={sourceReady}; receiver4k={received4k}; loopbackBytes={bytes}.");
            Log("Scope limit: only a successful run could prove virtual output format/NVENC transport; this does not prove original physical sensor resolution or image quality. A failed source-readiness run provides no encoded/received 4K proof.");
            lock(gate)File.WriteAllLines(evidence,messages);
            Console.WriteLine($"{(result==0?"PASS":"FAIL")} native virtual-camera 4K loopback; duration={watch.Elapsed.TotalSeconds:0.000}s; sourceReady={sourceReady}; received4k={received4k}; bytes={bytes}");
            Console.WriteLine("Metadata evidence: "+evidence);
        }
        return result;
    }
}
