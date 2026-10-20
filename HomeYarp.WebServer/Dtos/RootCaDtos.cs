using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HomeYarp.Domain;

namespace HomeYarp.WebServer.Dtos;

public sealed record RootCaResponse(
    string Subject,
    string Thumbprint,
    string Sha256Fingerprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string CertificatePem)
{
    public static RootCaResponse From(RootCertificateAuthority ca)
    {
        using var cert = X509Certificate2.CreateFromPem(ca.CertificatePem);
        return new RootCaResponse(
            ca.Subject,
            ca.Thumbprint,
            Convert.ToHexString(SHA256.HashData(cert.RawData)),
            ca.NotBefore,
            ca.NotAfter,
            ca.CertificatePem);
    }
}
