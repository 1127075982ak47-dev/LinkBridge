using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using LinkBridge.Core.Security;
using LinkBridge.Media;
using NAudio.Wave;

namespace PairedTlsProbe;

internal static class AudioLoopbackProbe
{
    internal static async Task RunAsync(string[] args)
    {
        static void Assert(bool condition,string name){if(!condition)throw new Exception(name);}
        var secret=PairedTlsTransport.SecretBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        using var certificate=PairedTlsTransport.CreateServerCertificate("LBA1");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start(8);var port=((IPEndPoint)listener.LocalEndpoint).Port;
        async Task<SslStream> Server(string context){var tcp=await listener.AcceptTcpClientAsync(deadline.Token);var ssl=new SslStream(tcp.GetStream(),false);try{await PairedTlsTransport.AuthenticateServerAsync(ssl,certificate,secret,context,()=>true,deadline.Token);return ssl;}catch{tcp.Dispose();ssl.Dispose();throw;}}
        async Task<SslStream> Client(byte[] key,string context){var tcp=new TcpClient();await tcp.ConnectAsync("127.0.0.1",port,deadline.Token);var ssl=new SslStream(tcp.GetStream(),false);try{await PairedTlsTransport.AuthenticateClientAsync(ssl,key,context,deadline.Token);return ssl;}catch{tcp.Dispose();ssl.Dispose();throw;}}
        var pcm=new byte[400000];new Random(1591).NextBytes(pcm);
        var actual=new byte[pcm.Length];
        var receive=Task.Run(async()=>
        {
         using var stream=await Server("LBA1");var provider=new BufferedWaveProvider(new WaveFormat(48000,16,2)){BufferLength=pcm.Length+4,ReadFully=false,DiscardOnBufferOverflow=false};
         var frames=new PcmFrameAssembler();var data=new byte[3840];int received=0;
         while(received<pcm.Length){var count=await stream.ReadAsync(data,deadline.Token);Assert(count>0,"PCM EOF before complete");frames.Push(provider,data,0,count);received+=count;Assert(provider.BufferedBytes%4==0,"TLS PCM frame alignment");}
         Assert(frames.PendingBytes==0&&provider.Read(actual,0,actual.Length)==pcm.Length,"exact complete encrypted PCM length");Console.WriteLine("NEGOTIATED "+stream.SslProtocol);
        });
        using(var stream=await Client(secret,"LBA1"))
        {
         await stream.WriteAsync(pcm.AsMemory(0,3),deadline.Token);await stream.WriteAsync(pcm.AsMemory(3,5),deadline.Token);
         var random=new Random(34);int offset=8;while(offset<pcm.Length){var count=Math.Min(random.Next(1,3839),pcm.Length-offset);await stream.WriteAsync(pcm.AsMemory(offset,count),deadline.Token);offset+=count;}
        }
        await receive;Assert(actual.SequenceEqual(pcm),"400000 encrypted PCM bytes remain exact, without handshake ACK contaminating sample alignment");Console.WriteLine("PASS LBA1 encrypted PCM 3+5 fragments/random lengths: 400000 bytes exact; no audio device.");
        async Task Negative(string serverContext,string clientContext,byte[] key,string label)
        {
         var server=Task.Run(async()=>{try{using var ssl=await Server(serverContext);throw new Exception("invalid client accepted");}catch(Exception ex)when(ex is AuthenticationException or IOException){}});
         try{using var ssl=await Client(key,clientContext);throw new Exception("invalid service accepted");}catch(Exception ex)when(ex is AuthenticationException or IOException){}
         await server;Console.WriteLine("PASS "+label);
        }
        await Negative("LBA1","LBA1",RandomNumberGenerator.GetBytes(32),"audio wrong key rejected");
        await Negative("LBA1","LBI3",secret,"input proof cannot authenticate audio service context");
        await Negative("LBI3","LBA1",secret,"audio proof cannot authenticate input service context");
        // Forwarding a real audio challenge under a different TLS certificate must fail.
        var front=new TcpListener(IPAddress.Loopback,0);front.Start();var frontPort=((IPEndPoint)front.LocalEndpoint).Port;
        using var attackerCert=PairedTlsTransport.CreateServerCertificate("LBA1");
        var backend=Task.Run(async()=>{try{using var ssl=await Server("LBA1");}catch(Exception ex)when(ex is AuthenticationException or IOException){}});
        var relay=Task.Run(async()=>
        {
         using var downTcp=await front.AcceptTcpClientAsync(deadline.Token);using var down=new SslStream(downTcp.GetStream(),false);
         await down.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=attackerCert,EnabledSslProtocols=OperatingSystem.IsMacOS()?SslProtocols.Tls12:SslProtocols.Tls12|SslProtocols.Tls13},deadline.Token);
         using var upTcp=new TcpClient();await upTcp.ConnectAsync("127.0.0.1",port,deadline.Token);using var up=new SslStream(upTcp.GetStream(),false,(_,_,_,_)=>true);
         await up.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="linkbridge-paired",EnabledSslProtocols=OperatingSystem.IsMacOS()?SslProtocols.Tls12:SslProtocols.Tls12|SslProtocols.Tls13},deadline.Token);
         var hello=new byte[36];await down.ReadExactlyAsync(hello,deadline.Token);await up.WriteAsync(hello,deadline.Token);
         var challenge=new byte[68];await up.ReadExactlyAsync(challenge,deadline.Token);await down.WriteAsync(challenge,deadline.Token);
         try{var proof=new byte[32];await down.ReadExactlyAsync(proof,deadline.Token);throw new Exception("audio client disclosed proof to replacement certificate");}catch(EndOfStreamException){}
        });
        using(var tcp=new TcpClient()){await tcp.ConnectAsync("127.0.0.1",frontPort,deadline.Token);using var ssl=new SslStream(tcp.GetStream(),false);try{await PairedTlsTransport.AuthenticateClientAsync(ssl,secret,"LBA1",deadline.Token);throw new Exception("audio MITM accepted");}catch(AuthenticationException){}}
        await Task.WhenAll(backend,relay);front.Stop();Console.WriteLine("PASS audio challenge relay with certificate substitution rejected.");
        var idle=Task.Run(async()=>{using var ssl=await Server("LBA1");using var wait=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);wait.CancelAfter(TimeSpan.FromSeconds(8));var clock=Stopwatch.StartNew();try{await ssl.ReadExactlyAsync(new byte[4],wait.Token);throw new Exception("idle PCM read unexpectedly returned");}catch(OperationCanceledException){Assert(clock.Elapsed.TotalSeconds>=7.5&&clock.Elapsed.TotalSeconds<9,"8s audio idle deadline");Console.WriteLine($"PASS encrypted PCM idle timeout at {clock.Elapsed.TotalSeconds:F2}s.");}});
        using(var ssl=await Client(secret,"LBA1")){await idle;}
        listener.Stop();CryptographicOperations.ZeroMemory(secret);Console.WriteLine("PASS audio TLS disposal; no sound-card change, physical capture or production port.");
        
    }
}

