using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HomeYarp.Tests.TestHelpers;

public static class CertificateFactory
{
    public static (string CertPem, string KeyPem) GenerateSelfSignedPem(params string[] hostnames)
    {
        if (hostnames.Length == 0)
        {
            hostnames = new[] { "test.local" };
        }

        var sanBuilder = new SubjectAlternativeNameBuilder();
        foreach (var host in hostnames)
        {
            sanBuilder.AddDnsName(host);
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={hostnames[0]}"),
            ecdsa,
            HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
            critical: false));
        request.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddYears(1);

        using var cert = request.CreateSelfSigned(notBefore, notAfter);
        var certPem = cert.ExportCertificatePem();
        var keyPem = ecdsa.ExportPkcs8PrivateKeyPem();
        return (certPem, keyPem);
    }
}
