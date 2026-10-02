using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkBridge.Core.Discovery;

static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
static async Task Until(Func<bool> predicate,int ms=3000){var clock=Stopwatch.StartNew();while(!predicate()&&clock.ElapsedMilliseconds<ms)await Task.Delay(10);Assert(predicate(),"state wait timed out");}
var ip=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType==NetworkInterfaceType.Ethernet&&
 !n.Description.Contains("Virtual",StringComparison.OrdinalIgnoreCase)&&!n.Name.Contains("Tailscale",StringComparison.OrdinalIgnoreCase)).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address).First(a=>a.AddressFamily==AddressFamily.InterNetwork);
using var reserve=new UdpClient(new IPEndPoint(IPAddress.Any,0));var port=((IPEndPoint)reserve.Client.LocalEndPoint!).Port;reserve.Dispose();
await using var discovery=new PeerDiscovery(45679,port){PairingSecret=Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),IsDiscoverable=false,HasSharedMicrophoneInput=true};
using var raw=new UdpClient(new IPEndPoint(ip,0));var source=(IPEndPoint)raw.Client.LocalEndPoint!;
var handle=typeof(PeerDiscovery).GetMethod("HandlePacket",BindingFlags.NonPublic|BindingFlags.Instance)!;
void Advertise(IPEndPoint endpoint,bool mic=true)=>handle.Invoke(discovery,[Encoding.UTF8.GetBytes("LINKBRIDGE/1"+JsonSerializer.Serialize(new{i="mic-control-fixture",n="fixture-"+endpoint.Address,p="Windows",mp=45679,miin=mic})),endpoint]);
Advertise(source);Assert(discovery.Snapshot().Single().HasSharedMicrophoneInput,"metadata mic endpoint hint parsed");
string Sign(string kind,string nonce,string stamp,int destination,string result="")=>Convert.ToHexString(HMACSHA256.HashData(Encoding.ASCII.GetBytes(discovery.PairingSecret),Encoding.UTF8.GetBytes($"{kind}|{nonce}|{stamp}|{destination}|{result}")));
long latest=0;
(byte[] Bytes,string Nonce,string Stamp) Packet(string kind,int destination,long? time=null,string? nonce=null,string? signature=null)
{
 var stamp=(time??(latest=Math.Max(latest+1,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))).ToString();nonce??=Guid.NewGuid().ToString("N");
 return(Encoding.UTF8.GetBytes($"LINKBRIDGE/1 {kind} {nonce} {stamp} {destination} "+(signature??Sign(kind,nonce,stamp,destination))),nonce,stamp);
}
void Feed(byte[] bytes,IPEndPoint? from=null){Advertise(from??source);handle.Invoke(discovery,[bytes,from??source]);}
async Task<string> ReceiveRaw(string nonce,int timeout=3000)
{
 using var deadline=new CancellationTokenSource(timeout);
 while(true){var packet=await raw.ReceiveAsync(deadline.Token);var text=Encoding.UTF8.GetString(packet.Buffer);if(text.Contains(nonce))return text;}
}
bool ValidReply(string text,string kind,string nonce,string stamp,int destination,string result)
{
 return text==$"LINKBRIDGE/1 {kind} {nonce} {stamp} {destination} {result} "+Sign(kind,nonce,stamp,destination,result);
}
int starts=0,stops=0,cameraStarts=0,cameraStops=0;string? active=null;int activePort=0;bool block=false;
var delayed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
discovery.SharedMicrophoneStartRequested+=(address,targetPort)=>
{
 Interlocked.Increment(ref starts);
 if(block)return delayed.Task.ContinueWith(t=>{active=address;activePort=targetPort;return t.Result;},TaskScheduler.Default);
 active=address;activePort=targetPort;return Task.FromResult(true);
};
discovery.SharedMicrophoneStopRequested+=address=>{active=null;activePort=0;Interlocked.Increment(ref stops);};
discovery.SharedCameraStartRequested+=(_,_)=>{Interlocked.Increment(ref cameraStarts);return Task.FromResult(true);};
discovery.SharedCameraStopRequested+=_=>Interlocked.Increment(ref cameraStops);discovery.Start();

Assert(await discovery.PrepareRemoteMicrophoneAsync(ip.ToString(),59684),"public MIC_START signed READY");Assert(starts==1&&activePort==59684,"source callback once");
Assert(await discovery.PrepareRemoteMicrophoneAsync(ip.ToString(),59684),"fresh same-port renewal READY");Assert(starts==1,"renewal does not invoke source callback");
Assert(await discovery.StopRemoteMicrophoneAsync(ip.ToString()),"public MIC_STOP signed STOPPED");Assert(active is null,"public stop synchronous");
Console.WriteLine("PASS public MIC_START/READY/STOP/STOPPED, authenticated nonce renewal reuses source.");

// Clock rejection and HMAC binding, on a fresh ordering timestamp after public API use.
latest=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+10;
var tampered=Packet("MIC_START",59684,signature:new string('0',64));Feed(tampered.Bytes);
Feed(Packet("MIC_START",59684,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-31000).Bytes);
Feed(Packet("MIC_START",59684,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+31000).Bytes);
Feed(Packet("MIC_START",59684,long.MinValue).Bytes);Feed(Packet("MIC_START",1).Bytes);
await Task.Delay(80);Assert(starts==1,"HMAC/extreme/expired/future/port rejected");
var unknown=new IPEndPoint(IPAddress.Parse("192.168.250.250"),source.Port);
handle.Invoke(discovery,[Packet("MIC_START",59684).Bytes,unknown]);await Task.Delay(50);Assert(starts==1,"unknown peer cannot trigger capture even with valid signature");
Console.WriteLine("PASS HMAC, +/-30s time, invalid port and unknown peer rejection.");

var first=Packet("MIC_START",59684);var watch=Stopwatch.StartNew();Feed(first.Bytes);await Until(()=>starts==2);
Assert(ValidReply(await ReceiveRaw(first.Nonce),"MIC_READY",first.Nonce,first.Stamp,59684,"1"),"signed READY exact nonce/time/port/result");
await Task.Delay(4000);var renewal=Packet("MIC_START",59684);Feed(renewal.Bytes);Assert(ValidReply(await ReceiveRaw(renewal.Nonce),"MIC_READY",renewal.Nonce,renewal.Stamp,59684,"1"),"renewal READY");
await Task.Delay(7000);Feed(renewal.Bytes);await ReceiveRaw(renewal.Nonce);Assert(active is not null&&starts==2,"4sec renewal keeps source live past original 10sec");
await Until(()=>active is null,4500);Assert(watch.ElapsedMilliseconds is >=14000 and <16000,"10sec lease expiry after last fresh nonce");
var expiredReplyTask=ReceiveRaw(first.Nonce);Feed(first.Bytes);Assert(ValidReply(await expiredReplyTask,"MIC_READY",first.Nonce,first.Stamp,59684,"0"),"expired replay cannot revive source or cached READY");Assert(starts==2,"lease replay did not recapture");
Console.WriteLine($"PASS 4sec fresh renewal and duplicate nonce no-renewal: expired at {watch.ElapsedMilliseconds} ms without recapture.");

var second=Packet("MIC_START",59684);Feed(second.Bytes);await Until(()=>starts==3);await ReceiveRaw(second.Nonce);
var changed=Packet("MIC_START",59685);Feed(changed.Bytes);await Until(()=>starts==4);await ReceiveRaw(changed.Nonce);Assert(activePort==59685,"changed port replaces epoch");
var beforeStop=stops;var stop=Packet("MIC_STOP",0);Feed(stop.Bytes);Assert(active is null&&stops==beforeStop+1,"raw stop synchronous");await ReceiveRaw(stop.Nonce);
Feed(second.Bytes);await ReceiveRaw(second.Nonce);Assert(active is null&&starts==4,"old port start replay not resurrected");
// Unique nonce with an earlier millisecond timestamp is an out-of-order request, not a replay.
Feed(Packet("MIC_START",59684,long.Parse(stop.Stamp)-1).Bytes);await Task.Delay(80);Assert(starts==4,"out-of-order fresh-nonce start after STOP rejected");
Console.WriteLine("PASS destination port epochs and millisecond ordering prevent late START resurrection after STOP.");

// A slow async start must not stop UDP discovery or camera; cancel-before-completion
// then queue a new source. Late old completion is cleaned before the new callback.
block=true;var pending=Packet("MIC_START",59686);var elapsed=Stopwatch.StartNew();Feed(pending.Bytes);Assert(elapsed.ElapsedMilliseconds<100,"MIC callback must be off listener");await Until(()=>starts==5);
Advertise(source);Assert(await discovery.PrepareRemoteCameraAsync(ip.ToString(),59683),"camera works while mic callback pending");Assert(cameraStarts==1,"camera separate START");
for(var retry=0;retry<100;retry++)Feed(pending.Bytes);
var gate=typeof(PeerDiscovery).GetField("_gate",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(discovery)!;
int Tasks(){lock(gate)return ((System.Collections.Generic.HashSet<Task>)typeof(PeerDiscovery).GetField("_microphoneTasks",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(discovery)!).Count;}
Assert(Tasks()<8,"100 duplicate pending retries do not allocate 100 reply tasks");
for(var fresh=0;fresh<100;fresh++)Feed(Packet("MIC_START",59686).Bytes);
Assert(Tasks()<=64,"authenticated pending requests have a bounded task budget");
var cancel=Packet("MIC_STOP",0);Feed(cancel.Bytes);await ReceiveRaw(cancel.Nonce);
await Until(()=>Tasks()<8);
block=false;var newer=Packet("MIC_START",59687);Feed(newer.Bytes);await Task.Delay(100);Assert(starts==5,"new callback waits for older callback cleanup");
delayed.TrySetResult(true);await Until(()=>starts==6&&activePort==59687);await ReceiveRaw(newer.Nonce);
Assert(await discovery.StopRemoteCameraAsync(ip.ToString()),"camera STOP while mic active");Assert(activePort==59687&&cameraStops==1,"camera STOP cannot stop microphone");
var finalStop=Packet("MIC_STOP",0);Feed(finalStop.Bytes);await ReceiveRaw(finalStop.Nonce);Assert(active is null,"mic STOP independent");
Console.WriteLine("PASS async cancellation/late completion/new START serialization, discovery and camera controls independent.");
Console.WriteLine("PASS 100 duplicate pending requests deduplicated; 100 fresh pending requests bounded to 64 tasks; STOP takes priority.");

// Moving to another paired owner retires the previous lease; its later STOP cannot
// cancel the active source owned by the replacement address.
var ownerA=Packet("MIC_START",59684);Feed(ownerA.Bytes);await Until(()=>starts==7);await ReceiveRaw(ownerA.Nonce);
using var otherSocket=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"),0));var other=(IPEndPoint)otherSocket.Client.LocalEndPoint!;
var ownerB=Packet("MIC_START",59685);Feed(ownerB.Bytes,other);await Until(()=>starts==8&&active=="127.0.0.2");var oldOwnerStop=Packet("MIC_STOP",0);Feed(oldOwnerStop.Bytes);await ReceiveRaw(oldOwnerStop.Nonce);Assert(active=="127.0.0.2","old owner STOP cannot stop replacement");
Console.WriteLine("PASS source-owner IP isolation.");
Assert(!discovery.ReportSharedMicrophoneSourceEnded(ip.ToString(),59684),"old sender-ended event cannot invalidate current owner");
Assert(discovery.ReportSharedMicrophoneSourceEnded("127.0.0.2",59685)&&active is null,"source EOF invalidates cached READY");
// Switch from the manual timestamp generator back to the public controller only
// after wall clock catches up with its intentionally incremented fixture stamps.
var fixtureClockGap=latest-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();if(fixtureClockGap>=0)await Task.Delay((int)fixtureClockGap+2);
Advertise(source);Assert(await discovery.PrepareRemoteMicrophoneAsync(ip.ToString(),59684),"fresh source reconnect after unexpected stream-ended");Assert(starts==9,"reconnect reopens source only once");
Assert(!discovery.ReportSharedMicrophoneSourceEnded(ip.ToString(),59685),"wrong old port EOF ignored");
Assert(discovery.ReportSharedMicrophoneSourceEnded(ip.ToString(),59684)&&active is null,"current port source EOF accepted");
Assert(await discovery.PrepareRemoteMicrophoneAsync(ip.ToString(),59684)&&starts==10,"next fresh nonce cannot reuse dead source READY");
Console.WriteLine("PASS source-ended current owner/port guard invalidates READY and subsequent nonce reopens source.");
await discovery.DisposeAsync();Assert(active is null,"dispose stops source synchronously");

await using(var closing=new PeerDiscovery(45679,0){PairingSecret=discovery.PairingSecret,IsDiscoverable=false})
{
 var finished=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var invoked=false;var staleActive=false;var stopCount=0;
 closing.SharedMicrophoneStartRequested+=(_,_)=>{invoked=true;return finished.Task.ContinueWith(task=>{staleActive=true;return task.Result;},TaskScheduler.Default);};
 closing.SharedMicrophoneStopRequested+=_=>{staleActive=false;Interlocked.Increment(ref stopCount);};closing.Start();
 handle.Invoke(closing,[Encoding.UTF8.GetBytes("LINKBRIDGE/1"+JsonSerializer.Serialize(new{i="closing-mic-fixture",n="fixture",p="Windows",mp=45679})),source]);
 handle.Invoke(closing,[Packet("MIC_START",59684).Bytes,source]);await Until(()=>invoked);var closingWatch=Stopwatch.StartNew();await closing.DisposeAsync();
 Assert(closingWatch.ElapsedMilliseconds<3200&&stopCount==1&&!staleActive,"disposal bounded and synchronously stops pending callback");
 finished.SetResult(true);await Until(()=>stopCount>=2);Assert(!staleActive,"late disposed callback cannot revive source");
 Console.WriteLine("PASS bounded dispose and late callback cleanup without microphone resurrection.");
}
await using(var moving=new PeerDiscovery(45679,0){PairingSecret=discovery.PairingSecret,IsDiscoverable=false})
{
 var late=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);string? current=null;var calls=0;var releases=0;
 moving.SharedMicrophoneStartRequested+=(address,_)=>{var count=Interlocked.Increment(ref calls);if(count==1)return late.Task.ContinueWith(task=>{current=address;return task.Result;},TaskScheduler.Default);current=address;return Task.FromResult(true);};
 moving.SharedMicrophoneStopRequested+=_=>{current=null;Interlocked.Increment(ref releases);};moving.Start();
 void Known(IPEndPoint endpoint)=>handle.Invoke(moving,[Encoding.UTF8.GetBytes("LINKBRIDGE/1"+JsonSerializer.Serialize(new{i="moving-mic-fixture",n="fixture-"+endpoint.Address,p="Windows",mp=45679})),endpoint]);
 Known(source);Known(other);handle.Invoke(moving,[Packet("MIC_START",59684).Bytes,source]);await Until(()=>calls==1);
 handle.Invoke(moving,[Packet("MIC_START",59685).Bytes,other]);Assert(releases==1,"new owner retires in-flight old source synchronously");
 late.SetResult(true);await Until(()=>calls==2&&current=="127.0.0.2");Assert(releases>=2,"old late start cleaned before new callback");
 handle.Invoke(moving,[Packet("MIC_STOP",0).Bytes,source]);Assert(current=="127.0.0.2","retired in-flight owner's STOP cannot affect replacement");
 await moving.DisposeAsync();Assert(current is null,"moving source disposed");Console.WriteLine("PASS owner handoff during pending START invalidates old epoch before its late completion.");
}
Console.WriteLine("PASS microphone source lease and signed controls; no physical device/driver/production port used.");
