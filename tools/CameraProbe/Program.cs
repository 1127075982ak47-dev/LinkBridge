using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LinkBridge.Core;
using LinkBridge.Media.Camera;
if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
static void Assert(bool ok,string name){if(!ok)throw new Exception(name);}
static async Task Until(Func<bool> predicate,int ms=15000){var clock=Stopwatch.StartNew();while(!predicate()&&clock.ElapsedMilliseconds<ms)await Task.Delay(50);Assert(predicate(),"condition timeout");}
const int frameBytes=33177600; const long mappingBytes=4096L+frameBytes*2;
var suffix=Guid.NewGuid().ToString("N");var mappingName="Local\\LinkBridge.Camera.BrokerTest."+suffix;var pipeName="LinkBridge.Camera.BrokerTest."+suffix;
using var mapping=MemoryMappedFile.CreateNew(mappingName,mappingBytes);using var view=mapping.CreateViewAccessor();
view.WriteArray(0,Encoding.ASCII.GetBytes("LBCAM01\0"),0,8);
view.Write(8,1);view.Write(12,4096);view.Write(16,3840);view.Write(20,2160);view.Write(24,15360);view.Write(28,0x41524742);view.Write(32,(long)frameBytes);
using var socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));var port=((IPEndPoint)socket.Client.LocalEndPoint!).Port;socket.Dispose();
var initial=Process.GetProcessesByName("ffmpeg").Select(p=>p.Id).ToHashSet();
await using var broker=new SharedCameraBroker(port,mappingName,pipeName,TimeSpan.FromSeconds(2));
var counts=new List<int>();var logs=new System.Collections.Concurrent.ConcurrentQueue<string>();broker.ConsumersChanged+=count=>{lock(counts)counts.Add(count);}; broker.BrokerLogEmitted+=line=>{logs.Enqueue(line);Console.WriteLine(line);};broker.Start();
Assert(broker.ConsumerCount==0&&broker.PublishedFrames==0,"no eager capture or fake frames");
try{await using var duplicate=new SharedCameraBroker(port,mappingName,pipeName);duplicate.Start();throw new Exception("duplicate broker allowed");}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){Console.WriteLine("PASS exclusive pipe owner");}
var a=Guid.NewGuid();var b=Guid.NewGuid();
async Task Send(Guid id,string action,int? pid=null){using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.Out,PipeOptions.Asynchronous);await pipe.ConnectAsync(3000);var json=JsonSerializer.Serialize(new{v=1,@event=action,instance=id.ToString("B"),pid=pid??Environment.ProcessId,width=3840,height=2160,fps=30})+"\n";await pipe.WriteAsync(Encoding.UTF8.GetBytes(json));}
await Send(a,"start",Environment.ProcessId+1);await Task.Delay(100);Assert(broker.ConsumerCount==0,"PID spoof rejected");
await Send(a,"start");await Until(()=>broker.ConsumerCount==1);await Send(b,"start");await Until(()=>broker.ConsumerCount==2);
Assert(broker.ExpectedSourceAddress is null&&broker.Status==SharedCameraStatus.WaitingForFrames,"unbound peer retains demand but no capture");
Assert(!Process.GetProcessesByName("ffmpeg").Any(p=>!initial.Contains(p.Id)),"unbound source launches no decoder");
foreach(var bad in new[]{"::1","0.0.0.0","255.255.255.255","239.1.2.3","127.0.0.1&sources=0.0.0.0"})
{try{broker.ExpectedSourceAddress=bad;throw new Exception("invalid address allowed");}catch(ArgumentException){}}
broker.ExpectedSourceAddress="127.0.0.1";
Console.WriteLine("PASS unbound source cannot receive media; invalid/non-unicast/IPv6/URI-injection source rejected.");
using var pulseCts=new CancellationTokenSource();var pulse=Task.Run(async()=>{while(!pulseCts.IsCancellationRequested){await Send(a,"heartbeat");await Send(b,"heartbeat");await Task.Delay(500,pulseCts.Token);}});
Process StartSender(string address,int width=3840,int height=2160)
{
var info=new ProcessStartInfo(FfmpegLocator.ExecutablePath!){UseShellExecute=false,RedirectStandardError=true,CreateNoWindow=true};
foreach(var arg in new[]{"-hide_banner","-loglevel","error","-re","-f","lavfi","-i",$"color=c=0x335577:s={width}x{height}:r=5","-an","-c:v","libx264rgb","-crf","0","-preset","ultrafast","-tune","zerolatency","-g","5","-f","mpegts",$"udp://127.0.0.1:{port}?pkt_size=1316&localaddr={address}"})info.ArgumentList.Add(arg);
var process=Process.Start(info)!;process.ErrorDataReceived+=(_,e)=>{if(e.Data!=null)Console.WriteLine("SENDER "+e.Data);};process.BeginErrorReadLine();return process;
}
async Task StopSender(Process process){ProcessHelper.TryKill(process);await process.WaitForExitAsync();process.Dispose();}
Process? sender=null;
try
{
 sender=StartSender("127.0.0.2");await Task.Delay(2500);Assert(broker.PublishedFrames==0,"unpaired source packets rejected before decode");await StopSender(sender);sender=null;
 Console.WriteLine("PASS real 4K UDP from 127.0.0.2 blocked while paired source is 127.0.0.1.");
 sender=StartSender("127.0.0.1");
 await Until(()=>broker.PublishedFrames>=4,20000);
 Assert(broker.Status==SharedCameraStatus.Connected,"real frames set connected");
 var snapshot=new byte[frameBytes];bool stable=false;
 for(int attempt=0;attempt<100;attempt++)
 {
  var before=view.ReadInt64(40);var slot=view.ReadInt32(48);if(before<=0||(before&1)!=0){await Task.Delay(10);continue;}
  view.ReadArray(4096L+slot*frameBytes,snapshot,0,snapshot.Length);Thread.MemoryBarrier();var after=view.ReadInt64(40);
  if(before==after&&(after&1)==0){stable=true;break;}
 }
 Assert(stable,"stable seqlock snapshot");Assert(view.ReadInt32(52)==1&&view.ReadInt64(64)==Environment.ProcessId,"IPC live status and producer PID");
 Assert(view.ReadInt64(56)>0&&Math.Abs(Environment.TickCount64-view.ReadInt64(72))<3000,"QPC timestamp and fresh heartbeat");
 var blue=snapshot[0];var green=snapshot[1];var red=snapshot[2];Assert(blue!=0&&green!=0&&red!=0&&snapshot[3]==255,"real synthetic BGRA pixels");
 for(int i=0;i<snapshot.Length;i+=4)Assert(snapshot[i]==blue&&snapshot[i+1]==green&&snapshot[i+2]==red&&snapshot[i+3]==255,"complete untorn 4K frame");
 Console.WriteLine($"PASS full 3840x2160 BGRA frame {frameBytes} bytes; RGB {red},{green},{blue}; published {broker.PublishedFrames}.");
 await StopSender(sender);sender=null;
 await Until(()=>broker.Status==SharedCameraStatus.WaitingForFrames&&view.ReadInt32(52)==0&&view.ReadInt64(72)==0,7000);
 Console.WriteLine("PASS live consumer heartbeats cannot retain Connected after >3 seconds without frames; mapping offline.");
 broker.ExpectedSourceAddress=null;var unbound=broker.PublishedFrames;sender=StartSender("127.0.0.1");await Task.Delay(1500);Assert(broker.PublishedFrames==unbound,"clear paired source retires decoder");await StopSender(sender);sender=null;
 broker.ExpectedSourceAddress="127.0.0.1";
 sender=StartSender("127.0.0.1",1920,1080);await Until(()=>logs.Any(line=>line.Contains("Invalid too big or non positive size")||line.Contains("Failed to configure input pad on Parsed_crop")),15000);Assert(broker.PublishedFrames==unbound&&broker.Status!=SharedCameraStatus.Connected,"1080 source rejected without upscaling");await StopSender(sender);sender=null;
 Console.WriteLine("PASS non-native 1920x1080 UDP rejected; no 4K frames fabricated by scaling.");
 broker.ExpectedSourceAddress="127.0.0.2";sender=StartSender("127.0.0.1");await Task.Delay(2000);Assert(broker.PublishedFrames==unbound,"switched source blocks former peer");await StopSender(sender);sender=null;
 sender=StartSender("127.0.0.2");await Until(()=>broker.PublishedFrames>=unbound+3,15000);
 Console.WriteLine("PASS peer switch rejects former peer and resumes native 4K from new paired source.");
 pulseCts.Cancel();try{await pulse;}catch(OperationCanceledException){}
 await Send(a,"stop");await Until(()=>broker.ConsumerCount==1);await Until(()=>broker.ConsumerCount==0,5000);
 Assert(view.ReadInt32(52)==0&&view.ReadInt64(72)==0,"lease expiry explicitly marks offline");
 var stopped=broker.PublishedFrames;await Task.Delay(350);Assert(stopped==broker.PublishedFrames,"no writes after last lease expires");
 Assert(counts.Contains(1)&&counts.Contains(2)&&counts.Last()==0,"consumer start/stop/expired counts");
 Console.WriteLine("PASS PID verification, two leases, heartbeat, stop-one and crash-expire-last, offline mapping, no stale writes.");
 await Task.WhenAll(Enumerable.Range(0,40).Select(async index=>{var id=Guid.NewGuid();await Send(id,"start");await Send(id,"heartbeat");await Send(id,"stop");}));
 await Until(()=>broker.ConsumerCount==0,6000);
 Console.WriteLine("PASS 120 concurrent short notifications: listener survives; stop/expiry reaches zero.");
}
finally{pulseCts.Cancel();try{await pulse;}catch{}if(sender is not null)await StopSender(sender);await broker.DisposeAsync();}
await Task.Delay(500);Assert(!Process.GetProcessesByName("ffmpeg").Any(p=>!initial.Contains(p.Id)),"no FFmpeg orphans");Console.WriteLine("PASS bounded disposal and no FFmpeg orphans; no camera installation or physical capture.");


