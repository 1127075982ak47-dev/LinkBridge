using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LinkBridge.Input;

namespace PairedTlsProbe;

internal static class InputLoopbackProbe
{
    internal static async Task RunAsync(string[] args)
    {
        static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
        static async Task Wait(Func<bool> check,int ms=3000){var watch=Stopwatch.StartNew();while(!check()&&watch.ElapsedMilliseconds<ms)await Task.Delay(15);Assert(check(),"timeout condition");}
        var secure=typeof(InputBridgeClient).Assembly.GetType("LinkBridge.Input.InputSecureHandshake")!;
        object? Invoke(string name,params object?[] args)=>secure.GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,args);
        var runSession=typeof(InputBridgeServer).GetMethod("RunSessionAsync",BindingFlags.NonPublic|BindingFlags.Static)!;
        var secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));var key=(byte[])Invoke("SecretBytes",secret)!;
        using var certificate=(X509Certificate2)Invoke("CreateServerCertificate")!;
        async Task<SslStream> AcceptSecure(TcpListener listener,bool allowed,CancellationToken ct)
        {
         var tcp=await listener.AcceptTcpClientAsync(ct);var tls=new SslStream(tcp.GetStream(),false);
         try{var accepted=await(Task<bool>)Invoke("AuthenticateServerAsync",tls,certificate,key,new Func<bool>(()=>allowed),ct)!;Assert(accepted==allowed,"permission result");return tls;}
         catch{tcp.Dispose();tls.Dispose();throw;}
        }
        var endpoint=new TcpListener(IPAddress.Loopback,0);endpoint.Start(8);int port=((IPEndPoint)endpoint.LocalEndpoint).Port;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));int applied=0,released=0;
        var normal=Task.Run(async()=>{using var tls=await AcceptSecure(endpoint,true,deadline.Token);try{await(Task)runSession.Invoke(null,new object[]{tls,new Action<InputPacket>(_=>Interlocked.Increment(ref applied)),new Action(()=>Interlocked.Increment(ref released)),deadline.Token})!;}catch(Exception ex)when(ex is IOException or OperationCanceledException or AuthenticationException){}Console.WriteLine("NEGOTIATED "+tls.SslProtocol);});
        await using(var client=await InputBridgeClient.ConnectAsync("127.0.0.1",secret,port))
        {
         Assert(client.IsConnected,"secure client connected");
         for(int batch=0;batch<20;batch++){for(int n=0;n<100;n++)Assert(client.TrySend(new InputPacket(InputEventType.Move,0,0,1,2)),"healthy packet queue");await Wait(()=>applied>=(batch+1)*100);}
         await Task.Delay(1300);Assert(client.IsConnected,"TLS heartbeat remains healthy");
        }
        await normal.WaitAsync(TimeSpan.FromSeconds(3));Assert(applied==2000&&released==1,"2000 input packets exact and one EOF release");Console.WriteLine("PASS healthy TLS mutual authentication, 2000 ordered inputs, heartbeats, EOF releases.");
        var wrongServer=Task.Run(async()=>{try{using var tls=await AcceptSecure(endpoint,true,deadline.Token);throw new Exception("wrong-key authenticated");}catch(Exception ex)when(ex is AuthenticationException or IOException){}});
        try{await using var wrong=await InputBridgeClient.ConnectAsync("127.0.0.1",Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),port);throw new Exception("wrong secret accepted");}catch(AuthenticationException){}
        await wrongServer;Console.WriteLine("PASS wrong pairing key rejected before input; shared secret never sent.");
        var deniedServer=Task.Run(async()=>{using var tls=await AcceptSecure(endpoint,false,deadline.Token);});
        try{await using var denied=await InputBridgeClient.ConnectAsync("127.0.0.1",secret,port);throw new Exception("permission denied accepted");}catch(AuthenticationException){}
        await deniedServer;Console.WriteLine("PASS authenticated permissions denial.");
        // A TLS-terminating MITM can relay application records but presents a different certificate.
        var upstream=new TcpListener(IPAddress.Loopback,0);upstream.Start();int upstreamPort=((IPEndPoint)upstream.LocalEndpoint).Port;
        using var attackerCertificate=(X509Certificate2)Invoke("CreateServerCertificate")!;
        var trueServer=Task.Run(async()=>{try{using var tls=await AcceptSecure(upstream,true,deadline.Token);}catch(Exception ex)when(ex is IOException or AuthenticationException or OperationCanceledException){}});
        var attack=Task.Run(async()=>
        {
         using var downstreamTcp=await endpoint.AcceptTcpClientAsync(deadline.Token);using var downstream=new SslStream(downstreamTcp.GetStream(),false);
         await downstream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=attackerCertificate,EnabledSslProtocols=OperatingSystem.IsMacOS()?SslProtocols.Tls12:SslProtocols.Tls12|SslProtocols.Tls13},deadline.Token);
         using var upstreamTcp=new TcpClient();await upstreamTcp.ConnectAsync("127.0.0.1",upstreamPort,deadline.Token);using var toServer=new SslStream(upstreamTcp.GetStream(),false,(_,_,_,_)=>true);
         await toServer.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="linkbridge-input",EnabledSslProtocols=OperatingSystem.IsMacOS()?SslProtocols.Tls12:SslProtocols.Tls12|SslProtocols.Tls13},deadline.Token);
         var hello=new byte[36];await downstream.ReadExactlyAsync(hello,deadline.Token);await toServer.WriteAsync(hello,deadline.Token);
         var challenge=new byte[68];await toServer.ReadExactlyAsync(challenge,deadline.Token);await downstream.WriteAsync(challenge,deadline.Token);
         try{var proof=new byte[32];await downstream.ReadExactlyAsync(proof,deadline.Token);throw new Exception("client sent proof to replacement certificate");}catch(EndOfStreamException){}
        });
        try{await using var attacked=await InputBridgeClient.ConnectAsync("127.0.0.1",secret,port);throw new Exception("MITM accepted");}catch(AuthenticationException){}
        await Task.WhenAll(attack,trueServer).WaitAsync(TimeSpan.FromSeconds(3));upstream.Stop();Console.WriteLine("PASS TLS certificate substitution MITM relay rejected before client proof/input.");
        // Authenticated server stops responding: heartbeat deadline must restore local control.
        var silent=Task.Run(async()=>{using var tls=await AcceptSecure(endpoint,true,deadline.Token);await Task.Delay(7500,deadline.Token);});
        await using(var client=await InputBridgeClient.ConnectAsync("127.0.0.1",secret,port))
        {
         var elapsed=Stopwatch.StartNew();await Wait(()=>!client.IsConnected,7200);Assert(elapsed.Elapsed.TotalSeconds>=5.5&&elapsed.Elapsed.TotalSeconds<=7.2,"heartbeat six-second deadline");Console.WriteLine("PASS silent authenticated peer disconnect at "+elapsed.Elapsed.TotalSeconds.ToString("F2")+"s.");
        }
        await silent;
        var pressure=Task.Run(async()=>{using var tls=await AcceptSecure(endpoint,true,deadline.Token);await Task.Delay(1000,deadline.Token);});
        await using(var client=await InputBridgeClient.ConnectAsync("127.0.0.1",secret,port))
        {
         var failures=0;Parallel.For(0,100000,i=>{if(!client.TrySend(new InputPacket(InputEventType.Move,0,0,i,i)))Interlocked.Increment(ref failures);});
         Assert(client.PendingPackets<=InputProtocol.QueueCapacity,"bounded TLS queue");Assert(!client.IsConnected&&failures>0,"overflow closes encrypted session and recovers local input");
        }
        await pressure;Console.WriteLine("PASS encrypted queue pressure bounded to256, overflow disconnect, orderly disposal.");
        var relayListener=new TcpListener(IPAddress.Loopback,0);relayListener.Start();int relayPort=((IPEndPoint)relayListener.LocalEndpoint).Port;
        var wire=new List<byte>();int encryptedApplied=0;
        var encryptedServer=Task.Run(async()=>{using var tls=await AcceptSecure(endpoint,true,deadline.Token);try{await(Task)runSession.Invoke(null,new object[]{tls,new Action<InputPacket>(_=>Interlocked.Increment(ref encryptedApplied)),new Action(()=>{}),deadline.Token})!;}catch(IOException){}});
        var relay=Task.Run(async()=>
        {
         using var down=await relayListener.AcceptTcpClientAsync(deadline.Token);using var up=new TcpClient();await up.ConnectAsync("127.0.0.1",port,deadline.Token);
         async Task Forward(Stream source,Stream target){var bytes=new byte[4096];try{while(true){var count=await source.ReadAsync(bytes,deadline.Token);if(count==0)return;lock(wire)wire.AddRange(bytes.AsSpan(0,count).ToArray());await target.WriteAsync(bytes.AsMemory(0,count),deadline.Token);}}catch(Exception ex)when(ex is IOException or OperationCanceledException or ObjectDisposedException){}}
         var toServer=Forward(down.GetStream(),up.GetStream());var toClient=Forward(up.GetStream(),down.GetStream());await Task.WhenAny(toServer,toClient);down.Dispose();up.Dispose();await Task.WhenAll(toServer,toClient);
        });
        var uniquePacket=new InputPacket(InputEventType.Move,63,0xBEEF,0x11223344,0x55667788);
        await using(var client=await InputBridgeClient.ConnectAsync("127.0.0.1",secret,relayPort)){Assert(client.TrySend(uniquePacket),"captured encrypted input send");await Wait(()=>encryptedApplied==1);}
        await Task.WhenAll(encryptedServer,relay);relayListener.Stop();
        var packetBytes=new byte[12];uniquePacket.Write(packetBytes);
        bool HasSequence(byte[] source,byte[] sought){for(int index=0;index<=source.Length-sought.Length;index++)if(source.AsSpan(index,sought.Length).SequenceEqual(sought))return true;return false;}
        var captured=wire.ToArray();Assert(captured.Length>0&&!HasSequence(captured,key)&&!HasSequence(captured,packetBytes),"raw wire must not expose pairing secret or event payload");Console.WriteLine($"PASS raw TCP wire capture {captured.Length} bytes: no plaintext secret or distinctive input packet.");
        var stall=Task.Run(async()=>{using var tcp=await endpoint.AcceptTcpClientAsync(deadline.Token);await Task.Delay(4500,deadline.Token);});var stalledClock=Stopwatch.StartNew();
        try{await using var stalled=await InputBridgeClient.ConnectAsync("127.0.0.1",secret,port);throw new Exception("non-TLS peer accepted");}catch(OperationCanceledException){}
        Assert(stalledClock.Elapsed.TotalSeconds>=3.7&&stalledClock.Elapsed.TotalSeconds<4.8,"TLS/auth overall four-second deadline");await stall;Console.WriteLine("PASS stalled TLS peer rejected by four-second authentication deadline.");
        endpoint.Stop();CryptographicOperations.ZeroMemory(key);
        
        
    }
}

