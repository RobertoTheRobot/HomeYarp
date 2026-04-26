using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;

namespace HomeYarp.Application.Services;

public interface ICertificateService
{
    Task<IReadOnlyList<Certificate>> ListAsync(CancellationToken cancellationToken = default);

    Task<Certificate?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Certificate> UploadAsync(string name, string? friendlyName, CertificateMaterial material, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
