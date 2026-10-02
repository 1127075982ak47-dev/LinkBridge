using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkBridge.Core.Discovery;

static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
static async Task Until(Func<bool> condition,int ms=3000){var clock=Stopwatch.StartNew();while(!condition()&&clock.ElapsedMilliseconds<ms)await Task.Delay(10);Assert(condition(),"wait state timeout");}
static int Port(){using var socket=new UdpClient(new IPEndPoint(IPAddress.Any,0));return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;}
var discoveryPort=Port();var key=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
await using var discovery=new PeerDiscovery(45679,discoveryPort){PairingSecret=key,IsDiscoverable=false};
using var a=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"),0));using var b=new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"),0));
var target=new IPEndPoint(IPAddress.Loopback,discoveryPort);
int starts=0,stops=0,micStarts=0;string? active=null;int activePort=0;bool block=false;
var pending=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var logs=new List<string>();
discovery.LogEmitted+=line=>{lock(logs)logs.Add(line);Console.WriteLine(line);};
discovery.SharedCameraStartRequested+=(address,port)=>{Interlocked.Increment(ref starts);if(block)return pending.Task.ContinueWith(t=>{active=address;activePort=port;return t.Result;},TaskScheduler.Default);active=address;activePort=port;return Task.FromResult(true);};
discovery.SharedCameraStopRequested+=_=>{active=null;activePort=0;Interlocked.Increment(ref stops);};
discovery.SharedMicrophoneStartRequested+=(_,_)=>{Interlocked.Increment(ref micStarts);return Task.FromResult(true);};discovery.Start();
async Task Advertise(UdpClient client,int version=2)
{
 var bytes=Encoding.UTF8.GetBytes("LINKBRIDGE/1"+JsonSerializer.Serialize(new{i=Guid.NewGuid().ToString("N"),n="camera-fixture-"+client.Client.LocalEndPoint,p="test",mp=45679,cv=version}));
 var address=((IPEndPoint)client.Client.LocalEndPoint!).Address;await client.SendAsync(bytes,new IPEndPoint(IPAddress.IsLoopback(address)?IPAddress.Loopback:address,discoveryPort));
}
await Advertise(a);await Advertise(b);await Until(()=>discovery.Snapshot().Count==2);Assert(discovery.Snapshot().All(p=>p.CameraControlProtocolVersion==2),"version discovery hint");
var clocks=new Dictionary<UdpClient,long>();
string Sign(string kind,string nonce,string time,int port,string result="")=>Convert.ToHexString(HMACSHA256.HashData(Encoding.ASCII.GetBytes(key),Encoding.UTF8.GetBytes($"{kind}|{nonce}|{time}|{port}|{result}")));
(byte[] Bytes,string Nonce,string Time,int Port,string Kind) Make(UdpClient client,string kind,int port,long? time=null,string? nonce=null,string? signature=null)
{
 var stamp=time??Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),clocks.GetValueOrDefault(client)+1);clocks[client]=Math.Max(clocks.GetValueOrDefault(client),stamp);
 nonce??=Guid.NewGuid().ToString("N");var text=stamp.ToString();return(Encoding.UTF8.GetBytes($"LINKBRIDGE/1 {kind} {nonce} {text} {port} "+(signature??Sign(kind,nonce,text,port))),nonce,text,port,kind);
}
async Task Send(UdpClient client,byte[] bytes){await Advertise(client);await client.SendAsync(bytes,target);}
async Task<string> Reply(UdpClient client,string nonce,int timeout=3000)
{
 using var deadline=new CancellationTokenSource(timeout);
 while(true){var packet=await client.ReceiveAsync(deadline.Token);var text=Encoding.UTF8.GetString(packet.Buffer);if(text.Contains(nonce))return text;}
}
async Task<bool> Exchange(UdpClient client,(byte[] Bytes,string Nonce,string Time,int Port,string Kind) message)
{
 await Send(client,message.Bytes);var response=await Reply(client,message.Nonce);var fields=response.Split(' ');
 Assert(fields.Length==7&&fields[0]=="LINKBRIDGE/1","response field count");var kind=message.Kind.EndsWith("START")?"CAMERA2_READY":"CAMERA2_STOPPED";
 Assert(fields[1]==kind&&fields[2]==message.Nonce&&fields[3]==message.Time&&fields[4]==message.Port.ToString()&&fields[5] is "0" or "1"&&fields[6]==Sign(kind,message.Nonce,message.Time,message.Port,fields[5]),"exact signed response binding");return fields[5]=="1";
}
async Task Silent(UdpClient client,(byte[] Bytes,string Nonce,string Time,int Port,string Kind) message)
{
 await Send(client,message.Bytes);using var deadline=new CancellationTokenSource(250);
 try{await Reply(client,message.Nonce,250);throw new Exception("invalid/out-of-order packet got READY");}catch(OperationCanceledException){}
}

// Actual UDP arrival order deliberately differs from request creation order.
var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();var sameSecond=(now/1000)*1000+100;
var first=Make(a,"CAMERA2_START",59683,sameSecond);var neverSeenOld=Make(a,"CAMERA2_START",59683,sameSecond+1);
var stop=Make(a,"CAMERA2_STOP",0,sameSecond+2);
Assert(await Exchange(a,first)&&starts==1,"initial START");Assert(await Exchange(a,stop)&&active is null,"same-second STOP");
await Silent(a,neverSeenOld);Assert(starts==1&&active is null,"unseen late old START cannot reopen after STOP");
Assert(!await Exchange(a,first)&&starts==1,"cached old START returns not-ready after STOP");
Console.WriteLine("PASS actual UDP START→STOP→unseen old START in same second; no capture resurrection; cached replay not-ready.");

var running=Make(a,"CAMERA2_START",59683);Assert(await Exchange(a,running),"new START");var count=starts;
for(var retry=0;retry<20;retry++)await Send(a,running.Bytes);await Task.Delay(80);Assert(starts==count,"duplicate packets do not restart source");
var watch=Stopwatch.StartNew();await Task.Delay(4000);var renew=Make(a,"CAMERA2_START",59683);Assert(await Exchange(a,renew)&&starts==count,"fresh renewal reuses source");
await Task.Delay(7000);Assert(await Exchange(a,renew)&&active is not null&&starts==count,"duplicate renewal reply does not reopen");
await Until(()=>active is null,4500);Assert(watch.ElapsedMilliseconds is >=14000 and <16000,"lease expiry after only fresh renewal");
Assert(!await Exchange(a,running)&&starts==count,"lease expired old START remains not-ready");
Console.WriteLine($"PASS duplicate UDP packets and fresh 4s renewal reuse source; replay does not renew 10s lease, expired at {watch.ElapsedMilliseconds}ms.");

var invalid=Make(a,"CAMERA2_START",59683,signature:new string('0',64));await Silent(a,invalid);
await Silent(a,Make(a,"CAMERA2_START",59683,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-31000));
await Silent(a,Make(a,"CAMERA2_START",59683,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+31000));
await Silent(a,Make(a,"CAMERA2_START",1));Assert(starts==count,"bad proof/time/port rejected");
// Do not use the deliberately future-invalid fixture clock for subsequent valid packets.
clocks[a]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var anew=Make(a,"CAMERA2_START",59683);Assert(await Exchange(a,anew),"A reopen");
var btime=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-500;var bnew=Make(b,"CAMERA2_START",59684,btime);
Assert(await Exchange(b,bnew)&&active=="127.0.0.3","B timestamp independent from A's newer clock");
Assert(await Exchange(a,Make(a,"CAMERA2_STOP",0))&&active=="127.0.0.3","old owner STOP cannot stop B");
Assert(await Exchange(b,Make(b,"CAMERA2_STOP",0))&&active is null,"B stop");
Console.WriteLine("PASS invalid HMAC/time/port and independent peer ordering/owner STOP isolation.");

block=true;var delayedStart=Make(a,"CAMERA2_START",59685);await Send(a,delayedStart.Bytes);await Until(()=>starts==count+3);
Assert(await Exchange(a,Make(a,"CAMERA2_STOP",0))&&active is null,"STOP during async start");
block=false;var replacement=Make(b,"CAMERA2_START",59686);await Send(b,replacement.Bytes);await Task.Delay(60);
Assert(starts==count+3,"new callback waits for old async completion");pending.SetResult(true);await Reply(b,replacement.Nonce);await Until(()=>active=="127.0.0.3"&&activePort==59686);
Assert(!await Exchange(a,delayedStart)&&active=="127.0.0.3","late old callback and replay cannot stop replacement");
Assert(await Exchange(b,Make(b,"CAMERA2_STOP",0)),"replacement STOP");
Console.WriteLine("PASS asynchronous old START/STOP/late completion/new peer sequencing without overlap or resurrection.");

var legacySeconds=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var legacy=Make(a,"CAMERA_START",59683,legacySeconds);var beforeLegacy=starts;await Send(a,legacy.Bytes);await Task.Delay(80);
Assert(starts==beforeLegacy&&active is null&&discovery.LastCameraControlError?.Contains("旧版") is true,"signed legacy START explicitly rejected");
Console.WriteLine("PASS signed legacy CAMERA_START rejected with clear upgrade log; no silent downgrade.");

// Public API creates the same CAMERA2 packets over real UDP (self LAN route).
var ip=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType==NetworkInterfaceType.Ethernet&& !n.Description.Contains("Virtual",StringComparison.OrdinalIgnoreCase)&&!n.Name.Contains("Tailscale",StringComparison.OrdinalIgnoreCase)).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(x=>x.Address).First(x=>x.AddressFamily==AddressFamily.InterNetwork);
using var lan=new UdpClient(new IPEndPoint(ip,0));await Advertise(lan,0);await Until(()=>discovery.Snapshot().Any(p=>p.Address==ip.ToString()));
Assert(await discovery.PrepareRemoteCameraAsync(ip.ToString(),59683)&&discovery.LastCameraControlError is null,"unsigned missing hint cannot veto signed v2 exchange");
var oldVersion=discovery.GetSharedCameraSourceVersion(ip.ToString(),59683)??throw new Exception("missing current source epoch");
Assert(await discovery.StopRemoteCameraAsync(ip.ToString()),"public STOP");
Assert(await discovery.PrepareRemoteCameraAsync(ip.ToString(),59683),"same endpoint new epoch");
var newVersion=discovery.GetSharedCameraSourceVersion(ip.ToString(),59683)??throw new Exception("missing reopened epoch");
Assert(newVersion!=oldVersion&&!discovery.ReportSharedCameraSourceEnded(ip.ToString(),59683,oldVersion)&&active is not null,"old EOF cannot clear same address/port new epoch");
Assert(discovery.ReportSharedCameraSourceEnded(ip.ToString(),59683,newVersion)&&active is null,"current EOF invalidates cached readiness");
Console.WriteLine("PASS epoch-version EOF guard rejects old generation after same endpoint reopen and accepts current source termination.");
pending=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);block=true;var startCount=starts;var preparing=discovery.PrepareRemoteCameraAsync(ip.ToString(),59683);await Until(()=>starts==startCount+1);
Assert(await discovery.StopRemoteCameraAsync(ip.ToString()),"public STOP supersedes pending START");Assert(!await preparing,"superseded outgoing START reports false");
pending.SetResult(true);block=false;await Until(()=>active is null);await Task.Delay(100);Assert(active is null,"superseded public late source stays stopped");
Console.WriteLine("PASS public CAMERA2 API, authenticated proof overrides hint, STOP cancels pending outgoing START/retries.");

pending=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);block=true;var closing=Make(b,"CAMERA2_START",59683);var previous=starts;await Send(b,closing.Bytes);await Until(()=>starts==previous+1);
var shutdown=Stopwatch.StartNew();await discovery.DisposeAsync();Assert(shutdown.ElapsedMilliseconds<3200&&active is null,"bounded disposal stops source");pending.SetResult(true);await Task.Delay(100);Assert(active is null,"disposed async completion cleaned");
Console.WriteLine("PASS actual UDP ordered camera control; bounded close and late completion cleanup; no camera hardware/driver/production port used.");
