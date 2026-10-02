using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using LinkBridge.Core.Security;

namespace LinkBridge.Input;

/// <summary>Input-specific LBI3 facade; all authentication is implemented by the shared paired TLS transport.</summary>
internal static class InputSecureHandshake
{
    internal static readonly TimeSpan Timeout = PairedTlsTransport.Timeout;
    internal static byte[] SecretBytes(string secret) => PairedTlsTransport.SecretBytes(secret);
    internal static X509Certificate2 CreateServerCertificate() => PairedTlsTransport.CreateServerCertificate("LBI3");
    internal static Task AuthenticateClientAsync(SslStream stream, string address, byte[] secret, CancellationToken ct) =>
        PairedTlsTransport.AuthenticateClientAsync(stream, secret, "LBI3", ct);
    internal static Task<bool> AuthenticateServerAsync(SslStream stream, X509Certificate2 certificate, byte[] secret,
        Func<bool> isPermitted, CancellationToken ct) =>
        PairedTlsTransport.AuthenticateServerAsync(stream, certificate, secret, "LBI3", isPermitted, ct);
}
