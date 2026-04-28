using HomeYarp.Domain;

namespace HomeYarp.Application.SelfSigned;

public interface ISelfSignedCertificateService
{
    Task<Certificate> IssueAsync(
        string name,
        string? friendlyName,
        IReadOnlyList<string> hostnames,
        CertificateKeyType keyType,
        int validityDays,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null);

    Task<Certificate> RegenerateAsync(Guid certificateId, CancellationToken cancellationToken = default);

    Task<Certificate> RegenerateAsync(Guid certificateId, IReadOnlyList<string> hostnames, CancellationToken cancellationToken = default);
}
