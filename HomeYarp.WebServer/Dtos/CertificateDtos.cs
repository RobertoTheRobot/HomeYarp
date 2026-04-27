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
    AcmeMetadataResponse? Acme,
    SelfSignedMetadataResponse? SelfSigned);

public sealed record AcmeMetadataResponse(
    IReadOnlyList<string> Hostnames,
    string AccountEmail,
    string DirectoryUrl,
    string KeyType,
    DateTimeOffset IssuedAt,
    DateTimeOffset? RenewedAt);

public sealed record SelfSignedMetadataResponse(
    IReadOnlyList<string> Hostnames,
    string KeyType,
    int ValidityDays,
    DateTimeOffset IssuedAt,
    DateTimeOffset? RegeneratedAt);

public sealed record CertificateUploadRequest(
    string Name,
    string? FriendlyName,
    string CertificatePem,
    string PrivateKeyPem);

public sealed record AcmeIssueRequest(
    string Name,
    string? FriendlyName,
    IReadOnlyList<string> Hostnames);

public sealed record SelfSignedIssueRequest(
    string Name,
    string? FriendlyName,
    IReadOnlyList<string> Hostnames,
    CertificateKeyType? KeyType,
    int? ValidityDays);

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
            c.Acme.RenewedAt),
        c.SelfSigned is null ? null : new SelfSignedMetadataResponse(
            c.SelfSigned.Hostnames,
            c.SelfSigned.KeyType.ToString(),
            c.SelfSigned.ValidityDays,
            c.SelfSigned.IssuedAt,
            c.SelfSigned.RegeneratedAt));
}
