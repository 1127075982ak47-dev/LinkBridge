using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LinkBridge.Core.Workspace;
public sealed class WorkspaceTransport : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly SslStream _stream;
    private readonly X509Certificate2 _identity;
    private readonly SemaphoreSlim _write=new(1);
    public string PeerId {get;}
    private WorkspaceTransport(TcpClient client,SslStream stream,X509Certificate2 identity,string peerId)
    {_client=client;_stream=stream;_identity=identity;PeerId=peerId;}
    public static async Task<WorkspaceTransport> ConnectAsync(WorkspaceProfile profile,WorkspaceDevice peer,CancellationToken ct)
    {
        WorkspaceTopology.ValidateHost(peer.Address);
        var c=new TcpClient{NoDelay=true};var cert=WorkspaceStore.Identity();SslStream? tls=null;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await c.ConnectAsync(peer.Address.Trim('[',']'),profile.SharingPort,timeout.Token);
            tls=new SslStream(c.GetStream(),false,(_,remote,_,_)=>remote is not null&&string.Equals(remote.GetCertHashString(HashAlgorithmName.SHA256),peer.Fingerprint,StringComparison.OrdinalIgnoreCase));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="LinkBridge Workspace",ClientCertificates=[cert],EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,CertificateRevocationCheckMode=X509RevocationMode.NoCheck},timeout.Token);
            return new(c,tls,cert,peer.Id);
        }
        catch{tls?.Dispose();c.Dispose();cert.Dispose();throw;}
    }
    public static async Task<WorkspaceTransport> AcceptAsync(WorkspaceProfile profile,TcpClient client,CancellationToken ct)
    {
        client.NoDelay=true;var cert=WorkspaceStore.Identity();string? peerId=null;
        var tls=new SslStream(client.GetStream(),false,(_,remote,_,_)=>
        {if(remote is null)return false;var fingerprint=remote.GetCertHashString(HashAlgorithmName.SHA256);peerId=profile.Devices.FirstOrDefault(d=>d.Id!=profile.LocalDeviceId&&string.Equals(d.Fingerprint,fingerprint,StringComparison.OrdinalIgnoreCase))?.Id;return peerId is not null;});
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=cert,ClientCertificateRequired=true,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,CertificateRevocationCheckMode=X509RevocationMode.NoCheck},timeout.Token);
            if(peerId is null)throw new AuthenticationException("设备身份未信任。");return new(client,tls,cert,peerId);
        }
        catch{tls.Dispose();client.Dispose();cert.Dispose();throw;}
    }
    public Task SendJsonAsync<T>(byte type,T message,CancellationToken ct)=>SendAsync(type,JsonSerializer.SerializeToUtf8Bytes(message),ct);
    public async Task SendAsync(byte type,ReadOnlyMemory<byte> bytes,CancellationToken ct)
    {
        if(bytes.Length>32*1024*1024)throw new InvalidDataException("共享消息过大。");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _write.WaitAsync(timeout.Token);
        try {var header=new byte[5];header[0]=type;BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1),bytes.Length);await _stream.WriteAsync(header,timeout.Token);await _stream.WriteAsync(bytes,timeout.Token);}
        finally{_write.Release();}
    }
    public async Task<(byte Type,byte[] Data)> ReadAsync(CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var header=new byte[5];await _stream.ReadExactlyAsync(header,timeout.Token);var length=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if(length<0||length>32*1024*1024)throw new InvalidDataException("共享消息长度无效。");
        var data=new byte[length];await _stream.ReadExactlyAsync(data,timeout.Token);return(header[0],data);
    }
    public async ValueTask DisposeAsync(){_client.Dispose();await _stream.DisposeAsync();_identity.Dispose();}
}
