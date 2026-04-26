using HomeYarp.Domain;

namespace HomeYarp.WebServer.Dtos;

public sealed record CertificateResponse(
    Guid Id,
    string Name,
    string? FriendlyName,
    string Subject,
    string Issuer,
    string Thumbprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    IReadOnlyList<string> SubjectAlternativeNames,
    DateTimeOffset CreatedAt);

public sealed record CertificateUploadRequest(
    string Name,
    string? FriendlyName,
    string CertificatePem,
    string PrivateKeyPem);

public static class CertificateDtoMapper
{
    public static CertificateResponse ToResponse(Certificate c) => new(
        c.Id,
        c.Name,
        c.FriendlyName,
        c.Subject,
        c.Issuer,
        c.Thumbprint,
        c.NotBefore,
        c.NotAfter,
        c.SubjectAlternativeNames,
        c.CreatedAt);
}
