using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using LinkBridge.Core.Security;

namespace PairedTlsProbe;

internal static class CrossAudioClientProbe
{
    internal static async Task RunAsync(string[] args)
    {
        var key=PairedTlsTransport.SecretBytes(Console.ReadLine()??throw new Exception("Missing test key"));
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));using var tcp=new TcpClient();await tcp.ConnectAsync("127.0.0.1",int.Parse(args[0]),deadline.Token);
        using var ssl=new SslStream(tcp.GetStream(),false);await PairedTlsTransport.AuthenticateClientAsync(ssl,key,"LBA1",deadline.Token);
        var pcm=new byte[400000];new Random(12367).NextBytes(pcm);await ssl.WriteAsync(pcm.AsMemory(0,3),deadline.Token);await ssl.WriteAsync(pcm.AsMemory(3,5),deadline.Token);
        for(int offset=8;offset<pcm.Length;){var count=Math.Min(1387,pcm.Length-offset);await ssl.WriteAsync(pcm.AsMemory(offset,count),deadline.Token);offset+=count;}
        Console.WriteLine($"MAC AUDIO SENT TLS={ssl.SslProtocol} PCM={pcm.Length}");CryptographicOperations.ZeroMemory(key);
    }
}

