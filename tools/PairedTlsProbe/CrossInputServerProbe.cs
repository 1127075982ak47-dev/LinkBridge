using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using LinkBridge.Core.Security;
using LinkBridge.Input;

namespace PairedTlsProbe;

internal static class CrossInputServerProbe
{
    internal static async Task RunAsync(string[] args)
    {
        var key=PairedTlsTransport.SecretBytes(Console.ReadLine()??throw new Exception("No test key"));
        using var cert=PairedTlsTransport.CreateServerCertificate("LBI3");var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        Console.WriteLine("PORT "+((IPEndPoint)listener.LocalEndpoint).Port);Console.Out.Flush();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var tcp=await listener.AcceptTcpClientAsync(deadline.Token);using var ssl=new SslStream(tcp.GetStream(),false);
        await PairedTlsTransport.AuthenticateServerAsync(ssl,cert,key,"LBI3",()=>true,deadline.Token);
        var applied=0;var released=0;var session=typeof(InputBridgeServer).GetMethod("RunSessionAsync",BindingFlags.NonPublic|BindingFlags.Static)!;
        try{await(Task)session.Invoke(null,new object[]{ssl,new Action<InputPacket>(_=>applied++),new Action(()=>released++),deadline.Token})!;}catch(IOException){}
        Console.WriteLine($"CROSS PASS TLS={ssl.SslProtocol} fake-inputs={applied} releases={released}");listener.Stop();CryptographicOperations.ZeroMemory(key);
    }
}

