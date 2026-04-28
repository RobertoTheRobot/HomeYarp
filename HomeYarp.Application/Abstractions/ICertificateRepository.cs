using HomeYarp.Domain;
using Microsoft.Extensions.Primitives;

namespace HomeYarp.Application.Abstractions;

public interface ICertificateRepository
{
    Task<IReadOnlyList<Certificate>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Certificate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Certificate?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<CertificateMaterial?> GetMaterialAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(Certificate certificate, CertificateMaterial material, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    IChangeToken GetReloadToken();

    /// <summary>
    /// Fires the reload change token explicitly. Save/Delete no longer trigger reload
    /// automatically — see <see cref="IApplicationRepository.SignalReload"/>.
    /// </summary>
    void SignalReload();
}

public sealed record CertificateMaterial(string CertificatePem, string PrivateKeyPem);
