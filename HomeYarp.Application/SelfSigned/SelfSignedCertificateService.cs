using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;

namespace HomeYarp.Application.SelfSigned;

public sealed class SelfSignedCertificateService : ISelfSignedCertificateService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> CertGates = new();

    private readonly ICertificateRepository _certificates;
    private readonly TimeProvider _timeProvider;

    public SelfSignedCertificateService(
        ICertificateRepository certificates,
        TimeProvider timeProvider)
    {
        _certificates = certificates;
        _timeProvider = timeProvider;
    }

    public Task<Certificate> IssueAsync(
        string name,
        string? friendlyName,
        IReadOnlyList<string> hostnames,
        CertificateKeyType keyType,
        int validityDays,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Certificate name is required.", nameof(name));
        }
        ValidateHostnames(hostnames);
        if (validityDays <= 0)
        {
            throw new ArgumentException("Validity days must be greater than zero.", nameof(validityDays));
        }

        return IssueInternalAsync(name, friendlyName, hostnames, keyType, validityDays, cancellationToken);
    }

    public Task<Certificate> RegenerateAsync(Guid certificateId, CancellationToken cancellationToken = default)
        => RegenerateInternalAsync(certificateId, hostnames: null, cancellationToken);

    public Task<Certificate> RegenerateAsync(Guid certificateId, IReadOnlyList<string> hostnames, CancellationToken cancellationToken = default)
    {
        ValidateHostnames(hostnames);
        return RegenerateInternalAsync(certificateId, hostnames, cancellationToken);
    }

    private async Task<Certificate> RegenerateInternalAsync(Guid certificateId, IReadOnlyList<string>? hostnames, CancellationToken cancellationToken)
    {
        var existing = await _certificates.GetByIdAsync(certificateId, cancellationToken)
            ?? throw new ArgumentException($"Certificate '{certificateId}' not found.", nameof(certificateId));

        if (existing.SelfSigned is null)
        {
            throw new InvalidOperationException($"Certificate '{existing.Name}' is not self-signed.");
        }

        var hosts = (hostnames ?? existing.SelfSigned.Hostnames).ToList();

        return await GenerateAndSaveAsync(
            existing.Id,
            existing.Name,
            existing.FriendlyName,
            hosts,
            existing.SelfSigned.KeyType,
            existing.SelfSigned.ValidityDays,
            existing.CreatedAt,
            existingIssuedAt: existing.SelfSigned.IssuedAt,
            regenerating: true,
            cancellationToken);
    }

    private async Task<Certificate> IssueInternalAsync(
        string name,
        string? friendlyName,
        IReadOnlyList<string> hostnames,
        CertificateKeyType keyType,
        int validityDays,
        CancellationToken cancellationToken)
    {
        var existing = await _certificates.GetByNameAsync(name, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"A certificate named '{name}' already exists.");
        }

        return await GenerateAndSaveAsync(
            Guid.NewGuid(),
            name,
            friendlyName,
            hostnames.ToList(),
            keyType,
            validityDays,
            createdAt: null,
            existingIssuedAt: null,
            regenerating: false,
            cancellationToken);
    }

    private async Task<Certificate> GenerateAndSaveAsync(
        Guid certificateId,
        string name,
        string? friendlyName,
        List<string> hostnames,
        CertificateKeyType keyType,
        int validityDays,
        DateTimeOffset? createdAt,
        DateTimeOffset? existingIssuedAt,
        bool regenerating,
        CancellationToken cancellationToken)
    {
        var gate = CertGates.GetOrAdd(certificateId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var notBefore = now.AddMinutes(-5);
            var notAfter = now.AddDays(validityDays);

            var (certificatePem, privateKeyPem, snapshot) = Generate(hostnames, keyType, notBefore, notAfter);

            var certificate = new Certificate
            {
                Id = certificateId,
                Name = name,
                FriendlyName = friendlyName,
                Subject = snapshot.Subject,
                Issuer = snapshot.Issuer,
                Thumbprint = snapshot.Thumbprint,
                NotBefore = snapshot.NotBefore,
                NotAfter = snapshot.NotAfter,
                SubjectAlternativeNames = snapshot.SubjectAlternativeNames,
                CreatedAt = createdAt ?? now,
                SelfSigned = new SelfSignedMetadata
                {
                    Hostnames = hostnames,
                    KeyType = keyType,
                    ValidityDays = validityDays,
                    IssuedAt = existingIssuedAt ?? now,
                    RegeneratedAt = regenerating ? now : (DateTimeOffset?)null
                }
            };

            await _certificates.SaveAsync(
                certificate,
                new CertificateMaterial(certificatePem, privateKeyPem),
                cancellationToken);

            return certificate;
        }
        finally
        {
            gate.Release();
        }
    }

    private static (string CertificatePem, string PrivateKeyPem, CertificateMetadataSnapshot Snapshot) Generate(
        IReadOnlyList<string> hostnames,
        CertificateKeyType keyType,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        var subject = new X500DistinguishedName($"CN={hostnames[0]}");

        var sanBuilder = new SubjectAlternativeNameBuilder();
        foreach (var host in hostnames)
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                sanBuilder.AddIpAddress(ip);
            }
            else
            {
                sanBuilder.AddDnsName(host);
            }
        }

        if (keyType == CertificateKeyType.Rsa2048)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            ApplyExtensions(request, sanBuilder);
            using var cert = request.CreateSelfSigned(notBefore, notAfter);
            return Export(cert, rsa.ExportPkcs8PrivateKeyPem());
        }
        else
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);
            ApplyExtensions(request, sanBuilder);
            using var cert = request.CreateSelfSigned(notBefore, notAfter);
            return Export(cert, ecdsa.ExportPkcs8PrivateKeyPem());
        }
    }

    private static void ApplyExtensions(CertificateRequest request, SubjectAlternativeNameBuilder sanBuilder)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(sanBuilder.Build());
    }

    private static (string CertificatePem, string PrivateKeyPem, CertificateMetadataSnapshot Snapshot) Export(
        X509Certificate2 cert,
        string privateKeyPem)
    {
        var certPem = cert.ExportCertificatePem();
        var sans = cert.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var snapshot = new CertificateMetadataSnapshot(
            cert.Subject,
            cert.Issuer,
            cert.Thumbprint,
            new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            sans);

        return (certPem, privateKeyPem, snapshot);
    }

    private static void ValidateHostnames(IReadOnlyList<string> hostnames)
    {
        if (hostnames is null || hostnames.Count == 0)
        {
            throw new ArgumentException("At least one hostname is required.", nameof(hostnames));
        }
        foreach (var host in hostnames)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Hostnames cannot be empty.", nameof(hostnames));
            }
        }
    }

    private sealed record CertificateMetadataSnapshot(
        string Subject,
        string Issuer,
        string Thumbprint,
        DateTimeOffset NotBefore,
        DateTimeOffset NotAfter,
        List<string> SubjectAlternativeNames);
}
