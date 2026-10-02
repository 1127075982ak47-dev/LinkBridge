using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using LinkBridge.Core.Security;

namespace LinkBridge.Core.Clipboard;

public sealed class ClipboardBridgeSender
{
    /// <summary>Returns only when the other computer applied the text/PNG; no source contents are logged.</summary>
    public async Task SendAsync(string address, string secret, ClipboardPayload payload, CancellationToken ct = default,
        int port = ClipboardLimits.DefaultPort)
    {
        ClipboardLimits.ValidatePayload(payload);
        // Clipboard owners can replace buffers while the network operation is running.
        var snapshot = new ClipboardPayload(payload.Id, payload.Kind, payload.Data.ToArray());
        var key = PairedTlsTransport.SecretBytes(secret);
        using var client = new TcpClient { NoDelay = true };
        try
        {
            using (var authentication = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                authentication.CancelAfter(PairedTlsTransport.Timeout);
                await client.ConnectAsync(address, port, authentication.Token).ConfigureAwait(false);
            }
            using var stream = new SslStream(client.GetStream(), false);
            using (var authentication = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                authentication.CancelAfter(PairedTlsTransport.Timeout);
                await PairedTlsTransport.AuthenticateClientAsync(stream, key, ClipboardLimits.ProtocolContext, authentication.Token).ConfigureAwait(false);
            }
            using var transfer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            transfer.CancelAfter(ClipboardLimits.TransferTimeout + ClipboardLimits.ApplyTimeout);
            await stream.WriteAsync(ClipboardWire.Header(snapshot), transfer.Token).ConfigureAwait(false);
            await stream.WriteAsync(snapshot.Data, transfer.Token).ConfigureAwait(false);
            await stream.FlushAsync(transfer.Token).ConfigureAwait(false);
            var reply = new byte[17]; await stream.ReadExactlyAsync(reply, transfer.Token).ConfigureAwait(false);
            if (new Guid(reply.AsSpan(0, 16)) != snapshot.Id || reply[16] != 1)
                throw new IOException("对方未能写入剪贴板，请检查连接状态。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(snapshot.Data);
        }
    }
}
