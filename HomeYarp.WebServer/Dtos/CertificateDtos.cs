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
    DateTimeOffset CreatedAt,
    AcmeMetadataResponse? Acme);

public sealed record AcmeMetadataResponse(
    IReadOnlyList<string> Hostnames,
    string AccountEmail,
    string DirectoryUrl,
    string KeyType,
    DateTimeOffset IssuedAt,
    DateTimeOffset? RenewedAt);

public sealed record CertificateUploadRequest(
    string Name,
    string? FriendlyName,
    string CertificatePem,
    string PrivateKeyPem);

public sealed record AcmeIssueRequest(
    string Name,
    string? FriendlyName,
    IReadOnlyList<string> Hostnames);

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
        c.CreatedAt,
        c.Acme is null ? null : new AcmeMetadataResponse(
            c.Acme.Hostnames,
            c.Acme.AccountEmail,
            c.Acme.DirectoryUrl,
            c.Acme.KeyType.ToString(),
            c.Acme.IssuedAt,
            c.Acme.RenewedAt));
}
