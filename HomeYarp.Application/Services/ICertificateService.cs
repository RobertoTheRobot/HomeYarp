using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;

namespace HomeYarp.Application.Services;

public interface ICertificateService
{
    Task<IReadOnlyList<Certificate>> ListAsync(CancellationToken cancellationToken = default);

    Task<Certificate?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the certificate's public PEM (the chain — no private key) so it can be
    /// downloaded and installed in client trust stores. Returns null when the id is unknown
    /// or the on-disk material has gone missing.
    /// </summary>
    Task<string?> GetCertificatePemAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Certificate> UploadAsync(string name, string? friendlyName, CertificateMaterial material, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
