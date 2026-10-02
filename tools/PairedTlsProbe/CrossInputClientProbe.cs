using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LinkBridge.Input;

namespace PairedTlsProbe;

internal static class CrossInputClientProbe
{
    internal static async Task RunAsync(string[] args)
    {
        if(args.Length!=2)throw new ArgumentException("cross-input-client requires SSH alias and remote PairedTlsProbe DLL path.");
        var alias=args[0]; var remoteDll=args[1];
        static string QuoteUnix(string value)=>"\u0027"+value.Replace("\u0027","\u0027\u0022\u0027\u0022\u0027")+"\u0027";
        var secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var serverInfo=new ProcessStartInfo("ssh"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-o","BatchMode=yes",alias,"dotnet "+QuoteUnix(remoteDll)+" cross-input-server"})serverInfo.ArgumentList.Add(arg);
        using var server=Process.Start(serverInfo)!;await server.StandardInput.WriteLineAsync(secret);await server.StandardInput.FlushAsync();
        var line=await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));if(line is null||!line.StartsWith("PORT "))throw new Exception("Mac server failed to start: "+line);var remotePort=int.Parse(line[5..]);
        using var free=new TcpListener(IPAddress.Loopback,0);free.Start();var localPort=((IPEndPoint)free.LocalEndpoint).Port;free.Stop();
        var forwardInfo=new ProcessStartInfo("ssh"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-o","BatchMode=yes","-o","ExitOnForwardFailure=yes","-N","-L",$"{localPort}:127.0.0.1:{remotePort}",alias})forwardInfo.ArgumentList.Add(arg);
        using var forward=Process.Start(forwardInfo)!;
        try
        {
         await Task.Delay(700);if(forward.HasExited)throw new Exception(await forward.StandardError.ReadToEndAsync());
         await using(var client=await InputBridgeClient.ConnectAsync("127.0.0.1",secret,localPort))
         {
          for(int batch=0;batch<20;batch++){for(int index=0;index<100;index++)if(!client.TrySend(new InputPacket(InputEventType.Move,0,0,1,2)))throw new Exception("Cross-platform send queue failed");await Task.Delay(40);}
          await Task.Delay(1200);if(!client.IsConnected)throw new Exception("Cross-platform TLS heartbeat failed");
         }
         await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));var output=await server.StandardOutput.ReadToEndAsync();var errors=await server.StandardError.ReadToEndAsync();
         if(server.ExitCode!=0||!output.Contains("fake-inputs=2000 releases=1"))throw new Exception("Mac gate failed: "+output+errors);
         Console.WriteLine(output.Trim());Console.WriteLine("PASS Windows SChannel client -> Mac SecureTransport server through isolated SSH tunnel; no native input injection.");
        }
        finally{if(!forward.HasExited)forward.Kill(true);if(!server.HasExited)server.Kill(true);}
    }
}

