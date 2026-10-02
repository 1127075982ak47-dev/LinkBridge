using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LinkBridge.Core.Security;

/// <summary>TLS plus mutual PSK proofs bound to the actual TLS server certificate and fresh nonces.</summary>
public static class PairedTlsTransport
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
    private const int NonceBytes = 32;
    // .NET 10 macOS Secure Transport supports TLS 1.2; other platforms also negotiate 1.3.
    private static SslProtocols EnabledProtocols => OperatingSystem.IsMacOS() ? SslProtocols.Tls12 : SslProtocols.Tls12 | SslProtocols.Tls13;
    public static byte[] SecretBytes(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != 32 || secret.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("配对密钥必须为32位可打印ASCII字符。", nameof(secret));
        return Encoding.ASCII.GetBytes(secret);
    }
    public static X509Certificate2 CreateServerCertificate(string protocolContext)
    {
        ValidateContext(protocolContext);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=LinkBridge Paired TLS " + protocolContext, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var usages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("linkbridge-paired");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        // SChannel cannot use an ephemeral CNG certificate key. Reimport without
        // PersistKeySet or a certificate-store write; the temporary key is deleted on dispose.
        var pfx = generated.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    public static async Task AuthenticateClientAsync(SslStream stream, byte[] secret, string protocolContext, CancellationToken ct = default)
    {
        var context = ValidateContext(protocolContext); ValidateSecret(secret);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "linkbridge-paired", EnabledSslProtocols = EnabledProtocols,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            AllowRenegotiation = false, AllowTlsResume = false,
            // The ephemeral certificate is authenticated by the PSK proof below. No application
            // byte is accepted or sent until that proof binds the actual presented certificate.
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null
        }, ct).ConfigureAwait(false);
        RequireEncryption(stream);
        var certificate = stream.RemoteCertificate ?? throw new AuthenticationException("对端没有提供TLS证书。");
        var fingerprint = SHA256.HashData(certificate.GetRawCertData());
        var clientNonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var hello = new byte[4 + NonceBytes]; context.CopyTo(hello); clientNonce.CopyTo(hello, 4);
        await stream.WriteAsync(hello, ct).ConfigureAwait(false);
        var challenge = new byte[4 + NonceBytes + 32]; await stream.ReadExactlyAsync(challenge, ct).ConfigureAwait(false);
        RequireContext(challenge.AsSpan(0, 4), context);
        var serverNonce = challenge.AsSpan(4, NonceBytes).ToArray();
        var expectedServer = Proof(secret, context, 0x53, fingerprint, clientNonce, serverNonce, 0);
        if (!CryptographicOperations.FixedTimeEquals(challenge.AsSpan(36, 32), expectedServer))
            throw new AuthenticationException("对端配对证明无效，可能是错误密钥或TLS证书被替换。");
        await stream.WriteAsync(Proof(secret, context, 0x43, fingerprint, clientNonce, serverNonce, 0), ct).ConfigureAwait(false);
        var result = new byte[4 + 1 + 32]; await stream.ReadExactlyAsync(result, ct).ConfigureAwait(false);
        RequireContext(result.AsSpan(0, 4), context);
        if (result[4] > 1 || !CryptographicOperations.FixedTimeEquals(result.AsSpan(5, 32),
            Proof(secret, context, 0x46, fingerprint, clientNonce, serverNonce, result[4])))
            throw new AuthenticationException("授权结果的配对证明无效。");
        if (result[4] != 1)
            throw new AuthenticationException("对端配对身份已确认，但未允许此服务。");
    }
    public static async Task<bool> AuthenticateServerAsync(SslStream stream, X509Certificate2 certificate, byte[] secret,
        string protocolContext, Func<bool> isPermitted, CancellationToken ct = default)
    {
        var context = ValidateContext(protocolContext); ValidateSecret(secret);
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate, ClientCertificateRequired = false,
            EnabledSslProtocols = EnabledProtocols, CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            AllowRenegotiation = false, AllowTlsResume = false
        }, ct).ConfigureAwait(false);
        RequireEncryption(stream);
        var hello = new byte[4 + NonceBytes]; await stream.ReadExactlyAsync(hello, ct).ConfigureAwait(false);
        RequireContext(hello.AsSpan(0, 4), context);
        var clientNonce = hello.AsSpan(4, NonceBytes).ToArray();
        var serverNonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var fingerprint = SHA256.HashData(certificate.RawData);
        var challenge = new byte[4 + NonceBytes + 32]; context.CopyTo(challenge);
        serverNonce.CopyTo(challenge, 4); Proof(secret, context, 0x53, fingerprint, clientNonce, serverNonce, 0).CopyTo(challenge, 36);
        await stream.WriteAsync(challenge, ct).ConfigureAwait(false);
        var candidate = new byte[32]; await stream.ReadExactlyAsync(candidate, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(candidate, Proof(secret, context, 0x43, fingerprint, clientNonce, serverNonce, 0)))
            throw new AuthenticationException("控制端未通过配对挑战认证。");
        var allowed = isPermitted() ? (byte)1 : (byte)0;
        var result = new byte[4 + 1 + 32]; context.CopyTo(result); result[4] = allowed;
        Proof(secret, context, 0x46, fingerprint, clientNonce, serverNonce, allowed).CopyTo(result, 5);
        await stream.WriteAsync(result, ct).ConfigureAwait(false);
        return allowed == 1;
    }
    private static byte[] Proof(byte[] secret, byte[] context, byte role, byte[] fingerprint, byte[] clientNonce, byte[] serverNonce, byte allowed)
    {
        var bytes = new byte[102]; context.CopyTo(bytes);
        bytes[4] = role; fingerprint.CopyTo(bytes, 5); clientNonce.CopyTo(bytes, 37); serverNonce.CopyTo(bytes, 69); bytes[101] = allowed;
        return HMACSHA256.HashData(secret, bytes);
    }
    private static byte[] ValidateContext(string context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Length != 4 || context.Any(character => !(character is >= 'A' and <= 'Z' or >= '0' and <= '9')))
            throw new ArgumentException("TLS服务上下文必须为4位大写ASCII字母或数字。", nameof(context));
        return Encoding.ASCII.GetBytes(context);
    }
    private static void ValidateSecret(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != 32) throw new ArgumentException("配对HMAC密钥必须为32字节。", nameof(secret));
    }
    private static void RequireContext(ReadOnlySpan<byte> header, ReadOnlySpan<byte> context)
    {
        if (!header.SequenceEqual(context))
            throw new AuthenticationException("加密服务协议上下文不一致，请同时更新两端应用。");
    }
    private static void RequireEncryption(SslStream stream)
    {
        if (!stream.IsEncrypted || !stream.IsSigned || stream.SslProtocol is not (SslProtocols.Tls12 or SslProtocols.Tls13))
            throw new AuthenticationException("配对服务需要TLS 1.2或TLS 1.3加密会话。");
    }
}



