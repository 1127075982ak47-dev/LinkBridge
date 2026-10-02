using System.Diagnostics;
using LinkBridge.Core.Models;
using LinkBridge.Media.Camera;

if(args.Contains("--native-source")) { Environment.Exit(await NativeLoopback.RunAsync()); return; }

void Assert(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
void Reject(Action action,string message){try{action();}catch(ArgumentException){Console.WriteLine("PASS "+message);return;}throw new Exception(message);}
var win=new MediaDevice(MediaDeviceKind.Camera,"Camera (NVIDIA Broadcast)","display name must not select device",MediaBackend.DirectShow);
var mac=new MediaDevice(MediaDeviceKind.Camera,"2","real Mac index",MediaBackend.AvFoundation);
var winArgs=SharedCameraProducer.BuildArguments(win,"192.0.2.2",56789).ToArray();
string Value(string[] values,string key)=>values[Array.IndexOf(values,key)+1];
Assert(Value(winArgs,"-f")=="dshow"&&Value(winArgs,"-i")=="video="+win.Id,"Windows selects actual DirectShow Id, not display name");
Assert(Value(winArgs,"-video_size")=="3840x2160"&&Value(winArgs,"-framerate")=="30","native Windows input requests 4K at 30fps");
Assert(Value(winArgs,"-c:v")=="h264_nvenc"&&Value(winArgs,"-b:v")=="16M","Windows hardware encoding requests NVENC 16M");
Assert(!winArgs.Contains("-s")&&!winArgs.Contains("-vf")&&!winArgs.Any(a=>a.Contains("scale=")),"no output scaling or upsample-to-4K filter");
var macArgs=SharedCameraProducer.BuildArguments(mac,"2001:db8::2",56789).ToArray();
Assert(Value(macArgs,"-f")=="avfoundation"&&Value(macArgs,"-i")=="2:none"&&Value(macArgs,"-c:v")=="h264_videotoolbox","Mac AVFoundation and VideoToolbox path preserved");
Assert(macArgs.Last().Contains("udp://[2001:db8::2]:56789"),"IPv6 target remains bracketed");
Reject(()=>SharedCameraProducer.BuildArguments(win with{IsApplicationEndpoint=true},"192.0.2.2",56789),"virtual application camera cannot be source");
Reject(()=>SharedCameraProducer.BuildArguments(win with{Id=""},"192.0.2.2",56789),"missing actual DirectShow Id rejected");
Reject(()=>SharedCameraProducer.BuildArguments(mac with{Id="camera name"},"192.0.2.2",56789),"Mac source requires actual index");
Reject(()=>SharedCameraProducer.BuildArguments(win,"bad-target",56789),"invalid target rejected before process creation");
Assert(SharedCameraProducer.TryParseInputDimensions("Stream #0:0: Video: rawvideo (UYVY / 0x59565955), uyvy422, 1920x1080, 30 fps",out var w,out var h)&&w==1920&&h==1080,"1080 input metadata cannot be confused with native 4K");
Assert(!SharedCameraProducer.TryParseFrameProgress("frame=0",out _)&&!SharedCameraProducer.TryParseFrameProgress("frame=invalid",out _),"zero or malformed progress cannot prove a frame");
Assert(SharedCameraProducer.TryParseFrameProgress("frame=1",out var frame)&&frame==1,"positive encoded progress is parsed independently of metadata");

ProcessStartInfo Synthetic(int width,int height,double duration=8)
{
 var info=new ProcessStartInfo(args.FirstOrDefault()??"ffmpeg"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
 foreach(var arg in new[]{"-hide_banner","-loglevel","info","-nostdin","-nostats","-progress","pipe:1","-re","-f","lavfi","-i",$"color=c=black:s={width}x{height}:r=30","-t",duration.ToString(System.Globalization.CultureInfo.InvariantCulture),"-an","-c:v","libx264","-preset","ultrafast","-b:v","16M","-pix_fmt","yuv420p","-f","null","-"})info.ArgumentList.Add(arg);
 return info;
}
var clock=Stopwatch.StartNew();
await using(var native=new SharedCameraProducer())
{
 var wrongBackend=OperatingSystem.IsWindows()?mac:win;
 try{await native.StartAsync(wrongBackend,"127.0.0.1",56789);throw new Exception("wrong OS backend should fail");}
 catch(PlatformNotSupportedException){Assert(!native.IsRunning,"native OS/backend mismatch rejected without opening camera");}
}
await using(var producer=new SharedCameraProducer(_=>Synthetic(1920,1080)))
{
 Assert(!await producer.StartAsync(win,"127.0.0.1",56789).WaitAsync(TimeSpan.FromSeconds(5)),"actual synthetic 1080 input rejected before ready");
 Assert(producer.LastError?.Contains("1920×1080")==true&&!producer.IsRunning,"1080 failure is explicit and cannot advertise 4K");
 await producer.StopAsync();
}
await using(var producer=new SharedCameraProducer(_=>Synthetic(3840,2160)))
{
 var ended=0;producer.SourceEnded+=(_,_,_)=>ended++;
 Assert(await producer.StartAsync(win,"127.0.0.1",56789).WaitAsync(TimeSpan.FromSeconds(5))&&producer.IsRunning,"actual 4K synthetic metadata plus encoded frame reaches ready");
 await producer.StopAsync();Assert(!producer.IsRunning&&ended==0,"explicit Stop prevents unexpected EOF notification");
}
await using(var producer=new SharedCameraProducer(_=>Synthetic(3840,2160,.4)))
{
 var ended=new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);producer.SourceEnded+=(_,_,generation)=>ended.TrySetResult(generation);
 Assert(await producer.StartAsync(mac,"127.0.0.1",56789).WaitAsync(TimeSpan.FromSeconds(5)),"injected Mac lifecycle keeps metadata/frame gate");
 var generation=producer.SourceGeneration;Assert(await ended.Task.WaitAsync(TimeSpan.FromSeconds(3))==generation&&!producer.IsRunning,"EOF carries exact source generation and clears ready state");
}
await using(var producer=new SharedCameraProducer(_=>throw new InvalidOperationException("fake source factory failure")))
{
 try{await producer.StartAsync(win,"127.0.0.1",56789);throw new Exception("factory should fail");}catch(InvalidOperationException){Assert(!producer.IsRunning,"factory failure leaves no running source");}
}
await using(var producer=new SharedCameraProducer(_=>Synthetic(3840,2160)))
{
 using var cancel=new CancellationTokenSource();var start=producer.StartAsync(win,"127.0.0.1",56789,cancel.Token);cancel.Cancel();
 try{await start;throw new Exception("canceled start should fail");}catch(OperationCanceledException){}
 await producer.StopAsync();Assert(!producer.IsRunning,"first-frame cancellation cannot retain source readiness");
}
Assert(clock.Elapsed<TimeSpan.FromSeconds(20),$"synthetic lifecycle probe completed in {clock.Elapsed.TotalSeconds:0.0}s");
Console.WriteLine("No camera was opened. Synthetic encoding used libx264; NVENC/native device support still requires real-device validation.");
