using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkBridge.Core;
using LinkBridge.Core.Discovery;
using LinkBridge.Media;

static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
static async Task Until(Func<bool> condition,int timeout=15000){var watch=Stopwatch.StartNew();while(!condition()&&watch.ElapsedMilliseconds<timeout)await Task.Delay(30);Assert(condition(),"condition timed out");}
static int FreePort(){using var socket=new UdpClient(new IPEndPoint(IPAddress.Any,0));return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;}
var port=FreePort();var initial=Process.GetProcessesByName("ffmpeg").Select(p=>p.Id).ToHashSet();var pids=new HashSet<int>();
int frames=0,ends=0;using var receiver=new MediaStreamReceiver();receiver.LogEmitted+=Console.WriteLine;
receiver.StreamEnded+=()=>Interlocked.Increment(ref ends);
receiver.FrameDecoded+=frame=>{using(frame){Assert(frame.Width==960&&frame.Height==540&&frame.Length==2073600,"unchanged 960x540 BGRA preview");Interlocked.Increment(ref frames);}};
int CurrentPid(){var type=typeof(MediaStreamReceiver);var value=type.GetField("_session",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(receiver);if(value is null)return 0;return ((Process)value.GetType().GetField("Process")!.GetValue(value)!).Id;}
void Track(){var id=CurrentPid();if(id!=0)pids.Add(id);}
Process Sender(string sourceIp,string destination="127.0.0.1")
{
 var info=new ProcessStartInfo(FfmpegLocator.ExecutablePath!){UseShellExecute=false,RedirectStandardError=true,CreateNoWindow=true};
 foreach(var arg in new[]{"-hide_banner","-loglevel","error","-re","-f","lavfi","-i","color=c=0x335577:s=1920x1080:r=10","-an","-c:v","libx264","-preset","ultrafast","-tune","zerolatency","-g","10","-f","mpegts",$"udp://{destination}:{port}?pkt_size=1316&localaddr={sourceIp}"})info.ArgumentList.Add(arg);
 var process=Process.Start(info)!;pids.Add(process.Id);process.BeginErrorReadLine();return process;
}
async Task StopSender(Process process){ProcessHelper.TryKill(process);await process.WaitForExitAsync();process.Dispose();}
receiver.Start(port);Assert(receiver.IsListeningRequested&&!receiver.IsRunning,"unbound startup keeps receive intent without a decoder");
Assert(!IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(e=>e.Port==port),"unbound startup has no UDP listener");
foreach(var bad in new[]{"::1","0.0.0.0","0.1.2.3","255.255.255.255","239.1.2.3","127.0.0.1&sources=0.0.0.0"}){try{receiver.ExpectedSourceAddress=bad;throw new Exception("bad source accepted");}catch(ArgumentException){}}
using(var sender=Sender("127.0.0.1")){await Task.Delay(500);Assert(frames==0&&!receiver.IsRunning,"unpaired UDP cannot cause preview decoding");await StopSender(sender);}
Console.WriteLine("PASS no paired source means no decoder/UDP listener/frames; invalid addresses rejected.");
receiver.ExpectedSourceAddress="127.0.0.1";Track();var firstPid=CurrentPid();receiver.Start(port);Assert(CurrentPid()==firstPid,"same-port normal Start reuses actual decoder");
using(var stranger=Sender("127.0.0.2")){await Task.Delay(2500);Assert(frames==0,"non-selected source IP rejected");await StopSender(stranger);}
using(var sender=Sender("127.0.0.1")){await Until(()=>frames>=4);receiver.Stop();await StopSender(sender);}
var stoppedFrames=Volatile.Read(ref frames);receiver.ExpectedSourceAddress="127.0.0.1";await Task.Delay(200);
Assert(!receiver.IsListeningRequested&&!receiver.IsRunning&&frames==stoppedFrames,"late binding cannot recreate stopped receive intent");
Assert(!receiver.TryAcceptAuthenticatedSource("127.0.0.1",port),"late authenticated callback cannot recreate stopped receive intent");
Assert(ends==0,"intentional stops emit no false StreamEnded");Console.WriteLine("PASS paired 1080 input preserves 960x540 preview; Stop blocks late bindings/old frames.");
receiver.Start(port);Track();var beforeRestart=CurrentPid();Assert(receiver.TryAcceptAuthenticatedSource("127.0.0.1",port,true),"authenticated reset accepted for requested port");Track();Assert(CurrentPid()!=beforeRestart,"fresh MEDIA_START forces decoder SPS reset");
Assert(!receiver.TryAcceptAuthenticatedSource("127.0.0.1",FreePort()),"wrong requested port rejected without changing decoder");
receiver.ExpectedSourceAddress=null;Assert(receiver.IsListeningRequested&&!receiver.IsRunning,"clear binding stops decoder while preserving waiting intent");
receiver.Stop();

// Exercise actual signed MEDIA_START source propagation through the public UDP API.
var ip=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType==NetworkInterfaceType.Ethernet&&
 !n.Description.Contains("Virtual",StringComparison.OrdinalIgnoreCase)&&!n.Name.Contains("Tailscale",StringComparison.OrdinalIgnoreCase)).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address).First(a=>a.AddressFamily==AddressFamily.InterNetwork);
var discoveryPort=FreePort();await using var discovery=new PeerDiscovery(port,discoveryPort){PairingSecret=Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),IsDiscoverable=false};
using var raw=new UdpClient(new IPEndPoint(ip,0));var endpoint=(IPEndPoint)raw.Client.LocalEndPoint!;
var handle=typeof(PeerDiscovery).GetMethod("HandlePacket",BindingFlags.NonPublic|BindingFlags.Instance)!;
handle.Invoke(discovery,[Encoding.UTF8.GetBytes("LINKBRIDGE/1"+JsonSerializer.Serialize(new{i="media-probe-fixture",n="paired-fixture",p="Windows",mp=port})),endpoint]);
int authenticated=0,legacy=0;string? passedAddress=null;
discovery.MediaStartRequested+=()=>{legacy++;return true;};
discovery.AuthenticatedMediaStartRequested+=address=>{authenticated++;passedAddress=address;var accepted=receiver.TryAcceptAuthenticatedSource(address,port);Track();return accepted;};discovery.Start();
var nonce=Guid.NewGuid().ToString("N");var timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
handle.Invoke(discovery,[Encoding.UTF8.GetBytes($"LINKBRIDGE/1 MEDIA_START {nonce} {timestamp} "+new string('0',64)),endpoint]);await Task.Delay(50);Assert(authenticated==0&&legacy==0,"bad HMAC invokes no callback");
receiver.Start(port);Assert(!receiver.IsRunning,"production waiting state before signed source");Assert(await discovery.PrepareRemoteReceiverAsync(ip.ToString()),"signed MEDIA_READY");
Assert(authenticated==1&&legacy==0&&passedAddress==ip.ToString()&&receiver.ExpectedSourceAddress==ip.ToString(),"production callback gets real source IP and wins over legacy");
var count=frames;using(var sender=Sender(ip.ToString(),ip.ToString())){await Until(()=>frames>=count+3);receiver.Stop();await StopSender(sender);}
Console.WriteLine("PASS signed MEDIA_START binds actual source IP; tampered request rejected; new callback takes priority; actual matching LAN frames decoded.");
var cache=(System.Collections.IDictionary)typeof(PeerDiscovery).GetField("_acceptedMediaNonces",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(discovery)!;
var entries=cache.GetEnumerator();Assert(entries.MoveNext()&&cache.Count==1,"one accepted media nonce");var acceptedEntry=entries.Entry;var acceptedNonce=((string)acceptedEntry.Key).Split(':')[^1];
var acceptedStamp=(string)acceptedEntry.Value!.GetType().GetProperty("Timestamp")!.GetValue(acceptedEntry.Value)!;
var acceptedPacket=Encoding.UTF8.GetBytes($"LINKBRIDGE/1 MEDIA_START {acceptedNonce} {acceptedStamp} "+Convert.ToHexString(HMACSHA256.HashData(Encoding.ASCII.GetBytes(discovery.PairingSecret),Encoding.UTF8.GetBytes($"START|{acceptedNonce}|{acceptedStamp}"))));
discovery.ReportMediaReceiverStopped();receiver.Stop();
async Task ReplayMustBeNotReady()
{
 handle.Invoke(discovery,[acceptedPacket,endpoint]);
 using var timeout=new CancellationTokenSource(300);
 try{await raw.ReceiveAsync(timeout.Token);throw new Exception("stopped nonce returned old READY");}catch(OperationCanceledException){}
 Assert(authenticated==1&&!receiver.IsRunning,"stopped nonce cannot invoke callback or reopen decoder");
}
await ReplayMustBeNotReady();receiver.Start(port);await ReplayMustBeNotReady();
Assert(cache.Count==1,"stopped accepted nonce remains in 65sec replay cache");
await Task.Delay(600);Assert(await discovery.PrepareRemoteReceiverAsync(ip.ToString())&&authenticated==2,"manual receive restart permits only a fresh nonce in new epoch");
discovery.ReportMediaReceiverStopped();receiver.Stop();
Console.WriteLine("PASS stop invalidates old cached READY without clearing nonce; old replay stays false after manual Start, fresh nonce resumes.");

// Concurrent lifecycle race: every cycle authorizes only explicit loopback and no
// operation can publish an old session after Stop returns.
await Task.WhenAll(Enumerable.Range(0,8).Select(worker=>Task.Run(async()=>{for(var cycle=0;cycle<8;cycle++){
 receiver.Start(port);receiver.TryAcceptAuthenticatedSource("127.0.0.1",port);await Task.Delay((worker+cycle)%7);receiver.Stop();
}})));
receiver.Stop();var finalFrames=Volatile.Read(ref frames);await Task.Delay(2000);
Assert(!receiver.IsRunning&&!receiver.IsListeningRequested&&Volatile.Read(ref frames)==finalFrames,"64 Start/Stop complete without stale frames");
Assert(ends==0,"no false StreamEnded from retired session");
Assert(!pids.Any(id=>{try{using var process=Process.GetProcessById(id);return !process.HasExited;}catch(ArgumentException){return false;}}),"tracked decoder/sender children all stopped");
Assert(!Process.GetProcessesByName("ffmpeg").Any(p=>!initial.Contains(p.Id)),"no new orphan FFmpeg process");
receiver.Dispose();try{receiver.ExpectedSourceAddress="127.0.0.1";throw new Exception("disposed binding accepted");}catch(ObjectDisposedException){}
Assert(!receiver.TryAcceptAuthenticatedSource("127.0.0.1",port),"disposed authenticated callback cannot reopen");
Console.WriteLine("PASS 64 concurrent explicit authenticated Start/Stop cycles; no stale frame, false end, orphan or disposed resurrection.");
