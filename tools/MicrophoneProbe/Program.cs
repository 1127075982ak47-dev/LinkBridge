using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using LinkBridge.Core.Security;
using LinkBridge.Media.Audio;

const string secret="ProbeOnly32ByteMicrophoneSecret!";
Assert(secret.Length==32,"test key length");
static void Assert(bool condition,string name){if(!condition)throw new Exception(name);}
static async Task Until(Func<bool> condition,int timeout=5000){var timer=Stopwatch.StartNew();while(!condition()&&timer.ElapsedMilliseconds<timeout)await Task.Delay(10);Assert(condition(),"wait condition timeout");}
static byte[] Header(){var bytes=new byte[12];BinaryPrimitives.WriteInt32LittleEndian(bytes,48000);BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4),16);BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6),2);BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8),4);return bytes;}
static async Task<(TcpClient,SslStream)> Client(int port,string key)
{
 var client=new TcpClient{NoDelay=true};using var deadline=new CancellationTokenSource(5000);
 try{await client.ConnectAsync(IPAddress.Loopback,port,deadline.Token);var tls=new SslStream(client.GetStream(),false);try{await PairedTlsTransport.AuthenticateClientAsync(tls,PairedTlsTransport.SecretBytes(key),"LBM1",deadline.Token);return(client,tls);}catch{tls.Dispose();throw;}}
 catch{client.Dispose();throw;}
}
var data=Enumerable.Range(0,76800).Select(i=>(byte)(i*31+17)).ToArray();
if(args.Length==2&&args[0]=="--serve")
{
 TestSink? received=null;
 await using var server=new SharedMicrophoneReceiver(secret,ip=>ip==args[1],()=>received=new TestSink(),port:0);
 server.LogEmitted+=Console.WriteLine;server.Start();Console.WriteLine("PROBE_PORT="+server.Port);
 await Until(()=>received?.Disposed==true,25000);Assert(received!.Bytes.SequenceEqual(data),"cross-machine PCM mismatch");
 Console.WriteLine($"PASS cross-machine LBM1 TLS {data.Length} exact synthetic bytes, max chunk {received.MaxWrite}, no real audio devices.");return;
}
if(args.Length==3&&args[0]=="--send")
{
 await using var sender=new SharedMicrophoneSender(int.Parse(args[2]));var ended=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);sender.StreamEnded+=()=>ended.TrySetResult();
 await sender.StartPcmAsync(args[1],secret,_=>ValueTask.FromResult<ISharedMicrophoneCapture>(new TestCapture(new FragmentStream(data))),true);
 await ended.Task.WaitAsync(TimeSpan.FromSeconds(8));Assert(sender.SentBytes==data.Length,"cross-machine sender truncated");Console.WriteLine("PASS cross-machine sender LBM1 TLS sent all synthetic bytes.");return;
}
var sinks=new List<TestSink>();var sinkGate=new object();var logs=new System.Collections.Concurrent.ConcurrentQueue<string>();
await using var receiver=new SharedMicrophoneReceiver(secret,ip=>ip=="127.0.0.1",()=>{var sink=new TestSink();lock(sinkGate)sinks.Add(sink);return sink;},port:0,bindAddress:IPAddress.Loopback);
receiver.LogEmitted+=line=>{logs.Enqueue(line);Console.WriteLine(line);};receiver.Start();
Assert(sinks.Count==0,"Start opens no sink");
var captureCalls=0;
await using(var sender=new SharedMicrophoneSender(receiver.Port))
{
 try{await sender.StartPcmAsync("127.0.0.1",secret,_=>{captureCalls++;return ValueTask.FromResult<ISharedMicrophoneCapture>(new TestCapture(new FragmentStream(data)));},false);throw new Exception("unrequested capture allowed");}catch(InvalidOperationException){}
 Assert(captureCalls==0&&sinks.Count==0,"no demand means no capture or sink");
 try{await sender.StartPcmAsync("127.0.0.1",new string('X',32),_=>{captureCalls++;return ValueTask.FromResult<ISharedMicrophoneCapture>(new TestCapture(new FragmentStream(data)));},true);throw new Exception("wrong key allowed");}catch(System.Security.Authentication.AuthenticationException){}
 Assert(captureCalls==0&&sinks.Count==0,"bad PSK creates no capture and no sink");
 Console.WriteLine("PASS no-demand and wrong-key requests do not capture or open an output.");
 TestCapture? actualCapture=null;
 await sender.StartPcmAsync("127.0.0.1",secret,_=>{captureCalls++;actualCapture=new TestCapture(new FragmentStream(data));return ValueTask.FromResult<ISharedMicrophoneCapture>(actualCapture);},true);
 await Until(()=>sinks.Count==1&&sinks[0].Disposed);
 Assert(sinks[0].Bytes.SequenceEqual(data),"source fragments preserve exact PCM order/channel alignment");Assert(sinks[0].MaxWrite<=3840,"network chunks bounded to 20ms");
 Assert(actualCapture!.Stopped&&actualCapture.Disposed&&!sender.IsRunning,"EOF releases capture");
 Console.WriteLine($"PASS {data.Length} exact synthetic PCM bytes; all sink writes divisible by 4, maximum {sinks[0].MaxWrite}, EOF cleaned source/sink.");
}

// Network-side independent fragmentation and EOF tail failure.
var before=sinks.Count;var (tcp,stream)=await Client(receiver.Port,secret);
using(tcp)using(stream)
{
 var wire=Header().Concat(data.Take(23)).ToArray();for(var i=0;i<wire.Length;i++)await stream.WriteAsync(wire.AsMemory(i,1));
}
await Until(()=>sinks.Count>before&&sinks[before].Disposed);
Assert(sinks[before].Bytes.SequenceEqual(data.Take(20)),"network fragments retain only complete frames");
Assert(logs.Any(line=>line.Contains("不完整麦克风PCM帧")),"truncated frame diagnosed");
Console.WriteLine("PASS one-byte TLS application fragments reassemble correctly; 3-byte EOF tail rejected.");

before=sinks.Count;(tcp,stream)=await Client(receiver.Port,secret);
using(tcp)using(stream){var header=Header();header[4]=24;await stream.WriteAsync(header);}
await Until(()=>sinks.Count>before&&sinks[before].Disposed);
Assert(sinks[before].Bytes.Count==0,"wrong PCM format rejected");Console.WriteLine("PASS non-16-bit PCM header rejected before sink receives samples.");

before=sinks.Count;(tcp,stream)=await Client(receiver.Port,secret);var idle=Stopwatch.StartNew();
using(tcp)using(stream)
{
 await stream.WriteAsync(Header());await Until(()=>sinks.Count>before&&sinks[before].Disposed,10000);
 Assert(idle.ElapsedMilliseconds is >=7500 and <10000,"8sec idle watchdog");
}
Console.WriteLine($"PASS authenticated idle connection releases virtual sink in {idle.ElapsedMilliseconds} ms.");

// Stop must kill capture synchronously, before the returned asynchronous cleanup.
await using(var sender=new SharedMicrophoneSender(receiver.Port))
{
 var blocked=new TestCapture(new BlockingStream());await sender.StartPcmAsync("127.0.0.1",secret,_=>ValueTask.FromResult<ISharedMicrophoneCapture>(blocked),true);
 var stop=sender.StopAsync();Assert(blocked.Stopped&&!sender.IsRunning,"synchronous capture stop");await stop;Assert(blocked.Disposed,"stop joins/disposes capture");
 Console.WriteLine("PASS StopAsync synchronously stops pending source read before awaiting cleanup.");
}
await using(var sender=new SharedMicrophoneSender(receiver.Port))
{
 var created=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
 var lateCapture=new TestCapture(new BlockingStream());
 var start=sender.StartPcmAsync("127.0.0.1",secret,async _=>{created.SetResult();await release.Task;return lateCapture;},true);
 await created.Task.WaitAsync(TimeSpan.FromSeconds(5));await sender.StopAsync();release.SetResult();
 try{await start;throw new Exception("stopped pending Start returned success");}catch(OperationCanceledException){}
 Assert(lateCapture.Stopped&&lateCapture.Disposed&&!sender.IsRunning,"late factory result retired");Console.WriteLine("PASS Stop during async capture factory rejects/cleans late result.");
}

await using(var denied=new SharedMicrophoneReceiver(secret,_=>true,()=>throw new Exception("sink must not open"),port:0,isDemandAllowed:()=>false,bindAddress:IPAddress.Loopback))
await using(var sender=new SharedMicrophoneSender())
{
 denied.Start();await using var directed=new SharedMicrophoneSender(denied.Port);var calls=0;
 try{await directed.StartPcmAsync("127.0.0.1",secret,_=>{calls++;return ValueTask.FromResult<ISharedMicrophoneCapture>(new TestCapture(new BlockingStream()));},true);throw new Exception("denied receiver allowed capture");}catch(System.Security.Authentication.AuthenticationException){}
 Assert(calls==0,"denied virtual mic route captures nothing");Console.WriteLine("PASS authenticated but denied virtual route does not start capture.");
}

// Actual paired TLS socket with a receiver that deliberately never drains PCM.
var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var stalledPort=((IPEndPoint)listener.LocalEndpoint).Port;
using var certificate=PairedTlsTransport.CreateServerCertificate("LBM1");using var stalledCancel=new CancellationTokenSource(10000);
var stalled=Task.Run(async()=>{using var client=await listener.AcceptTcpClientAsync(stalledCancel.Token);client.ReceiveBufferSize=4096;using var tls=new SslStream(client.GetStream(),false);await PairedTlsTransport.AuthenticateServerAsync(tls,certificate,PairedTlsTransport.SecretBytes(secret),"LBM1",()=>true,stalledCancel.Token);var header=new byte[12];await tls.ReadExactlyAsync(header,stalledCancel.Token);try{await Task.Delay(Timeout.Infinite,stalledCancel.Token);}catch(OperationCanceledException){}});
try
{
 await using var sender=new SharedMicrophoneSender(stalledPort);var ended=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);sender.StreamEnded+=()=>ended.TrySetResult();sender.LogEmitted+=Console.WriteLine;
 var capture=new TestCapture(new EndlessStream());var watch=Stopwatch.StartNew();await sender.StartPcmAsync("127.0.0.1",secret,_=>ValueTask.FromResult<ISharedMicrophoneCapture>(capture),true);
 await ended.Task.WaitAsync(TimeSpan.FromSeconds(8));Assert(capture.Stopped&&capture.Disposed,"backpressure closes capture");
 Console.WriteLine($"PASS unreading paired TLS peer closes capture after bounded write timeout: {watch.ElapsedMilliseconds} ms; {sender.SentBytes} bytes total, no sender queue.");
}
finally{stalledCancel.Cancel();listener.Stop();try{await stalled;}catch(OperationCanceledException){}}

var endpoint=SharedMicrophoneEndpoints.Diagnose();Console.WriteLine("ENDPOINT "+endpoint.Diagnostic);
if(!endpoint.Available){try{SharedMicrophoneEndpoints.CreateVirtualSink();throw new Exception("missing virtual route fell back to speaker");}catch(InvalidOperationException){}Console.WriteLine("PASS missing virtual backend gives explicit failure and no default-speaker fallback.");}
await receiver.DisposeAsync();Console.WriteLine("PASS paired microphone transport; no physical mic capture, driver install or production port used.");

sealed class TestSink : ISharedMicrophoneSink
{
 public string EndpointName=>"TEST ONLY bounded-call sink";public List<byte> Bytes{get;}=[];public bool Disposed;public int MaxWrite;
 public void Write(ReadOnlySpan<byte> pcm){if(pcm.Length%4!=0)throw new Exception("unaligned sink write");if(Disposed)throw new Exception("write after dispose");MaxWrite=Math.Max(MaxWrite,pcm.Length);Bytes.AddRange(pcm.ToArray());}
 public void Dispose()=>Disposed=true;
}
sealed class TestCapture(Stream stream) : ISharedMicrophoneCapture
{
 public Stream PcmStream=>stream;public bool Stopped,Disposed;
 public void Stop(){Stopped=true;stream.Dispose();}public void Dispose(){Disposed=true;Stop();}
}
abstract class ReadOnlyStream : Stream
{
 public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
 public override void Flush()=>throw new NotSupportedException();public override long Seek(long p,SeekOrigin o)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();
}
sealed class FragmentStream(byte[] data) : ReadOnlyStream
{
 private int _offset,_iteration;
 public override ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken ct=default){ct.ThrowIfCancellationRequested();var count=Math.Min(target.Length,Math.Min(data.Length-_offset,(_iteration++%5) switch{0=>1,1=>2,2=>3,3=>997,_=>3839}));data.AsMemory(_offset,count).CopyTo(target);_offset+=count;return ValueTask.FromResult(count);}
}
sealed class BlockingStream : ReadOnlyStream
{
 public override async ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken ct=default){await Task.Delay(Timeout.Infinite,ct);return 0;}
}
sealed class EndlessStream : ReadOnlyStream
{
 public override ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken ct=default){ct.ThrowIfCancellationRequested();target.Span.Fill(0x55);return ValueTask.FromResult(target.Length);}
}
