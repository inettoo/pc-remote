using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PcRemote.Transport;

public static class DesktopTls
{
    private static readonly SslApplicationProtocol ApplicationProtocol = new("pc-remote/3");

    public static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PC Remote LAN Host", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        // Windows Schannel needs a native key container; default import deletes it on disposal.
        byte[] pfx = certificate.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    public static async Task<SslStream> ClientAsync(Stream stream, CancellationToken cancellationToken)
    {
        // ADR 0007 deliberately trusts the entire on-link LAN: encryption, no peer authentication.
        // No certificate/PIN acceptance UI and no claim of first-contact MITM protection.
        var tls = new SslStream(stream, false, (_, certificate, _, _) => certificate is not null);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "pc-remote.local",
                EnabledSslProtocols = SslProtocols.Tls13,
                ApplicationProtocols = [ApplicationProtocol],
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken);
            VerifyProtocol(tls);
            return tls;
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    public static async Task<SslStream> ServerAsync(Stream stream, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        var tls = new SslStream(stream, false);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls13,
                ApplicationProtocols = [ApplicationProtocol],
                ClientCertificateRequired = false
            }, cancellationToken);
            VerifyProtocol(tls);
            return tls;
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    private static void VerifyProtocol(SslStream tls)
    {
        if (tls.NegotiatedApplicationProtocol != ApplicationProtocol)
            throw new AuthenticationException("Incompatible desktop protocol.");
    }
}
