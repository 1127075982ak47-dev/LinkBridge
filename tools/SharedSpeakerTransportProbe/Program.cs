using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using LinkBridge.Core.Security;
using LinkBridge.Media;

var clock=Stopwatch.StartNew();
var secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
var ffmpeg=args.FirstOrDefault()??"ffmpeg";
var childPids=new HashSet<int>();
void RememberCapture(SharedAudioSender sender)
{
 var capture=(Process?)typeof(SharedAudioSender).GetField("_capture",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(sender);
 if(capture is not null){try{childPids.Add(capture.Id);}catch(InvalidOperationException){}}
}
void Assert(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);}
async Task Fail(Task task,string name){try{await task.WaitAsync(TimeSpan.FromSeconds(5));}catch{Console.WriteLine("PASS "+name);return;}throw new Exception(name);}
async Task Until(Func<bool> check,int milliseconds=3500){var t=Stopwatch.StartNew();while(!check()){if(t.ElapsedMilliseconds>milliseconds)throw new TimeoutException();await Task.Delay(20);}}
ProcessStartInfo Synthetic(double duration=10,bool delayed=false)
{
 var info=new ProcessStartInfo(ffmpeg){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
 foreach(var a in new[]{"-hide_banner","-loglevel","error","-nostdin","-re","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t",duration.ToString(System.Globalization.CultureInfo.InvariantCulture)})info.ArgumentList.Add(a);
 if(delayed){info.ArgumentList.Add("-af");info.ArgumentList.Add("atrim=start=2,asetpts=PTS-STARTPTS");}
 foreach(var a in new[]{"-ac","2","-ar","48000","-c:a","pcm_s16le","-f","s16le","pipe:1"})info.ArgumentList.Add(a);
 return info;
}
foreach(var rejected in new[]{"wrong-key","output-refused"})
{
 await using var server=new TestPeer(secret,rejected!="output-refused");var captures=0;var take=0;var give=0;
 await using var sender=new SharedAudioSender(()=>{captures++;return Synthetic();},()=>take++,()=>give++,()=>{},server.Port);
 await Fail(sender.StartAsync("127.0.0.1",rejected=="wrong-key"?Convert.ToHexString(RandomNumberGenerator.GetBytes(16)):secret),rejected+" rejected by genuine paired TLS");
 Assert(captures==0&&take==0&&give==0&&!sender.IsRunning,rejected+" cannot start capture or route");
}
// Success/explicit Stop and duplicate Stop/Dispose must return the route exactly once.
await using(var server=new TestPeer(secret,true))
{
 var take=0;var give=0;var ended=0;
 var sender=new SharedAudioSender(()=>Synthetic(),()=>take++,()=>give++,()=>{},server.Port);sender.StreamEnded+=()=>ended++;
 await sender.StartAsync("127.0.0.1",secret);await Until(()=>server.Bytes>=4);
 RememberCapture(sender);
 Assert(sender.IsRunning&&sender.SentBytes>=4&&take==1,"first genuine PCM over TLS acquires route before ready");
 await sender.StopAsync();await sender.StopAsync();await sender.DisposeAsync();
 Assert(give==1&&ended==0&&!sender.IsRunning,"Stop plus duplicate Stop/Dispose restores once and suppresses EOF");
}
await using(var server=new TestPeer(secret,true))
{
 var take=0;var give=0;var ended=0;
 SharedAudioSender? sender=null;sender=new SharedAudioSender(()=>Synthetic(.4),()=>{take++;RememberCapture(sender!);},()=>give++,()=>{},server.Port);sender.StreamEnded+=()=>Interlocked.Increment(ref ended);
 await sender.StartAsync("127.0.0.1",secret);await Until(()=>Volatile.Read(ref ended)==1);
 await sender.DisposeAsync();Assert(take==1&&give==1&&!sender.IsRunning,"synthetic EOF returns route exactly once");
}
await using(var server=new TestPeer(secret,true))
{
 var take=0;var give=0;var sender=new SharedAudioSender(()=>Synthetic(),()=>take++,()=>give++,()=>{},server.Port);
 await sender.StartAsync("127.0.0.1",secret);await sender.DisposeAsync();await sender.DisposeAsync();
 Assert(take==1&&give==1&&!sender.IsRunning,"Dispose restores exactly once");
}
await using(var server=new TestPeer(secret,true))
{
 var take=0;var give=0;await using var sender=new SharedAudioSender(()=>Synthetic(10,true),()=>take++,()=>give++,()=>{},server.Port);
 using var cancel=new CancellationTokenSource(250);
 var delayedStart=sender.StartAsync("127.0.0.1",secret,cancel.Token);await Until(()=>typeof(SharedAudioSender).GetField("_capture",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(sender) is Process,2000);RememberCapture(sender);
 await Fail(delayedStart,"readiness wait cancellation surfaced");
 await Task.Delay(350);Assert(take==0&&give==0&&!sender.IsRunning&&server.Bytes==0,"cancel before delayed FFmpeg PCM cannot acquire route");
}
await using(var server=new TestPeer(secret,true))
{
 var take=0;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var release=new ManualResetEventSlim();
 await using var sender=new SharedAudioSender(()=>{entered.SetResult();release.Wait(TimeSpan.FromSeconds(2));return Synthetic();},()=>take++,()=>{},()=>{},server.Port);
 var start=sender.StartAsync("127.0.0.1",secret);await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
 await sender.StopAsync();release.Set();await Fail(start,"late capture factory completion is canceled");
 Assert(take==0&&sender.SentBytes==0&&!sender.IsRunning,"late factory cannot spawn or take route");
}
foreach(var failure in new[]{"factory","route"})
{
 await using var server=new TestPeer(secret,true);var take=0;var give=0;
 SharedAudioSender? observed=null;await using var sender=observed=new SharedAudioSender(()=>failure=="factory"?throw new InvalidOperationException("synthetic factory failure"):Synthetic(),()=>{take++;RememberCapture(observed!);throw new InvalidOperationException("fake route failure");},()=>give++,()=>{},server.Port);
 await Fail(sender.StartAsync("127.0.0.1",secret),failure+" failure surfaced");
 Assert(!sender.IsRunning&&(failure=="factory"?take==0&&give==0:take==1&&give==1),failure+" cleanup returns partial lease and leaves no ready state");
}
// External event handlers may fail but must not corrupt successful transport or restoration.
await using(var server=new TestPeer(secret,true))
{
 var give=0;await using var sender=new SharedAudioSender(()=>Synthetic(.4),()=>{},()=>give++,()=>{},server.Port);
 sender.LogEmitted+=_=>throw new InvalidOperationException("fake subscriber exception");
 await sender.StartAsync("127.0.0.1",secret);await Until(()=>give==1);
 Assert(!sender.IsRunning,"subscriber exception does not corrupt transport cleanup");
}
await using(var server=new TestPeer(secret,true))
{
 var attempts=0;await using var sender=new SharedAudioSender(()=>Synthetic(),()=>{},()=>{if(++attempts==1)throw new IOException("fake original output missing");},()=>{},server.Port);
 await sender.StartAsync("127.0.0.1",secret);RememberCapture(sender);await sender.StopAsync();
 Assert(sender.LastRouteError is not null&&attempts==1,"restore failure remains truthful and retryable");
 await sender.StopAsync();Assert(sender.LastRouteError is null&&attempts==2,"later Stop retries failed restoration");
}
await using(var server=new TestPeer(secret,true))
{
 var callback=new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
 await using var sender=new SharedAudioSender(()=>Synthetic(.4),()=>{},()=>{},()=>{},server.Port);
 sender.StreamEnded+=()=>{var watch=Stopwatch.StartNew();sender.StopAsync().AsTask().GetAwaiter().GetResult();callback.TrySetResult(watch.Elapsed.TotalMilliseconds);};
 await sender.StartAsync("127.0.0.1",secret);RememberCapture(sender);
 var elapsed=await callback.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert(elapsed<1000,$"EOF subscriber can synchronously Stop without pump self-wait ({elapsed:0}ms)");
}
foreach(var pid in childPids){try{using var child=Process.GetProcessById(pid);Assert(child.HasExited,$"synthetic process {pid} exited");}catch(ArgumentException){Console.WriteLine("PASS synthetic process "+pid+" no longer exists");}}
Assert(clock.Elapsed<TimeSpan.FromSeconds(45),$"bounded probe duration {clock.Elapsed.TotalSeconds:0.0}s");
Console.WriteLine("Actual TLS loopback and actual synthetic FFmpeg only; no sound card, route/default write or pairing secret printed.");

sealed class TestPeer:IAsyncDisposable
{
 readonly TcpListener listener=new(IPAddress.Loopback,0);readonly CancellationTokenSource lifetime=new();readonly Task task;
 readonly System.Security.Cryptography.X509Certificates.X509Certificate2 certificate=PairedTlsTransport.CreateServerCertificate("LBA2");readonly byte[] key;
 TcpClient? client;public long Bytes;public int Port=>(int)((IPEndPoint)listener.LocalEndpoint).Port;
 public TestPeer(string secret,bool permit){key=PairedTlsTransport.SecretBytes(secret);listener.Start();task=Task.Run(()=>Run(permit));}
 async Task Run(bool permit)
 {
  try{client=await listener.AcceptTcpClientAsync(lifetime.Token);using var tls=new SslStream(client.GetStream());
   if(!await PairedTlsTransport.AuthenticateServerAsync(tls,certificate,key,"LBA2",()=>permit,lifetime.Token))return;
   var data=new byte[8192];while(!lifetime.IsCancellationRequested){var count=await tls.ReadAsync(data,lifetime.Token);if(count==0)break;Interlocked.Add(ref Bytes,count);}
  }catch(Exception)when(lifetime.IsCancellationRequested){}catch(System.Security.Authentication.AuthenticationException){}catch(IOException){}finally{client?.Dispose();}
 }
 public async ValueTask DisposeAsync(){lifetime.Cancel();listener.Stop();client?.Dispose();try{await task.WaitAsync(TimeSpan.FromSeconds(2));}catch(OperationCanceledException){}finally{certificate.Dispose();CryptographicOperations.ZeroMemory(key);lifetime.Dispose();}}
}
