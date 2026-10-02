using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using LinkBridge.Core;
using LinkBridge.Input;

if(args.Length!=1 || !IPAddress.TryParse(args[0],out _)) throw new ArgumentException("Provide peer IP only; no key argument.");
// Authorized, read-only availability diagnosis: no settings Save/Load (Load also adjusts ACL),
// key printing, input hooks, native mouse/key packets, GUI or trust changes.
using var document=JsonDocument.Parse(File.ReadAllText(SettingsStore.FilePath));
var key=document.RootElement.GetProperty("inputPairingSecret").GetString() ?? "";
if(key.Length!=32){Console.WriteLine("UNCONFIGURED pairing credential missing; value not logged");return;}
using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(6));
try
{
 await using var connection=await InputBridgeClient.ConnectAsync(args[0],key,timeout.Token);
 await Task.Delay(250,timeout.Token); // transport heartbeat only, never an injected input event.
 Console.WriteLine($"{DateTime.UtcNow:o} AUTHENTICATED_PERMITTED connected={connection.IsConnected}; no user input sent");
}
catch(AuthenticationException ex) when(ex.Message.Contains("未允许此服务",StringComparison.Ordinal))
{Console.WriteLine($"{DateTime.UtcNow:o} AUTHENTICATED_PERMISSION_DENIED: current remote service authenticated but Mac accessibility permission is not granted");}
catch(AuthenticationException){Console.WriteLine($"{DateTime.UtcNow:o} AUTHENTICATION_FAILED: protocol/key/certificate proof rejected; no credential logged");}
catch(OperationCanceledException){Console.WriteLine($"{DateTime.UtcNow:o} CONNECT_OR_HANDSHAKE_TIMEOUT");}
catch(System.Net.Sockets.SocketException ex){Console.WriteLine($"{DateTime.UtcNow:o} SOCKET_FAILED code={ex.SocketErrorCode}");}
