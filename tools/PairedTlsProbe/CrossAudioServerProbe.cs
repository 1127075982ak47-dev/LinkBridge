using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using LinkBridge.Core.Security;
using LinkBridge.Media;
using NAudio.Wave;

namespace PairedTlsProbe;

internal static class CrossAudioServerProbe
{
    internal static async Task RunAsync(string[] args)
    {
        Process LaunchSsh(params string[] args){var info=new ProcessStartInfo("ssh"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)info.ArgumentList.Add(arg);return Process.Start(info)!;}
        if(args.Length!=2)throw new ArgumentException("cross-audio-server requires SSH alias and remote PairedTlsProbe DLL path.");
        var alias=args[0];var remoteDll=args[1];
        static string QuoteUnix(string value)=>"\u0027"+value.Replace("\u0027","\u0027\u0022\u0027\u0022\u0027")+"\u0027";
        var secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));var key=PairedTlsTransport.SecretBytes(secret);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var localPort=((IPEndPoint)listener.LocalEndpoint).Port;
        using var allocation=LaunchSsh("-o","BatchMode=yes",alias,"python3 -c 'import socket; s=socket.socket(); s.bind((\"127.0.0.1\",0)); print(s.getsockname()[1]); s.close()'");
        var remotePort=int.Parse((await allocation.StandardOutput.ReadLineAsync())!);await allocation.WaitForExitAsync();
        using var forward=LaunchSsh("-o","BatchMode=yes","-o","ExitOnForwardFailure=yes","-N","-R",$"{remotePort}:127.0.0.1:{localPort}",alias);
        Process? client=null;using var cert=PairedTlsTransport.CreateServerCertificate("LBA1");using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
         await Task.Delay(700);if(forward.HasExited)throw new Exception(await forward.StandardError.ReadToEndAsync());
         client=LaunchSsh("-o","BatchMode=yes",alias,$"dotnet {QuoteUnix(remoteDll)} cross-audio-client {remotePort}");await client.StandardInput.WriteLineAsync(secret);await client.StandardInput.FlushAsync();
         using var tcp=await listener.AcceptTcpClientAsync(deadline.Token);using var ssl=new SslStream(tcp.GetStream(),false);await PairedTlsTransport.AuthenticateServerAsync(ssl,cert,key,"LBA1",()=>true,deadline.Token);
         var buffer=new BufferedWaveProvider(new WaveFormat(48000,16,2)){BufferLength=400004,ReadFully=false,DiscardOnBufferOverflow=false};var framer=new PcmFrameAssembler();var bytes=new byte[3840];int received=0;
         while(received<400000){var count=await ssl.ReadAsync(bytes,deadline.Token);if(count==0)throw new Exception("premature PCM EOF");framer.Push(buffer,bytes,0,count);received+=count;}
         var pcm=new byte[400000];if(buffer.Read(pcm,0,pcm.Length)!=pcm.Length)throw new Exception("incorrect PCM count");var expected=new byte[400000];new Random(12367).NextBytes(expected);if(!pcm.SequenceEqual(expected))throw new Exception("encrypted cross-platform PCM mismatch");
         await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));var output=await client.StandardOutput.ReadToEndAsync();var errors=await client.StandardError.ReadToEndAsync();if(client.ExitCode!=0)throw new Exception(errors);
         Console.WriteLine(output.Trim());Console.WriteLine($"PASS Mac TLS client -> Windows SChannel server: {received} PCM bytes exact after 3+5/random fragments; TLS={ssl.SslProtocol}; no audio output/capture.");
        }
        finally{listener.Stop();if(!forward.HasExited)forward.Kill(true);if(client is not null){if(!client.HasExited)client.Kill(true);client.Dispose();}CryptographicOperations.ZeroMemory(key);}
    }
}

