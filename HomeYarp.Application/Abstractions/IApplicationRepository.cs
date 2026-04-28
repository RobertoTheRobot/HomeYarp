using HomeYarp.Domain;
using Microsoft.Extensions.Primitives;

namespace HomeYarp.Application.Abstractions;

public interface IApplicationRepository
{
    Task<IReadOnlyList<Domain.Application>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Domain.Application?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Domain.Application?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task AddAsync(Domain.Application application, CancellationToken cancellationToken = default);

    Task UpdateAsync(Domain.Application application, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    IChangeToken GetReloadToken();

    /// <summary>
    /// Fires the reload change token explicitly. Add/Update/Delete no longer trigger
    /// reload automatically — callers (the manual UI button, the renewal workers) decide
    /// when subscribers (HomeYarpConfigProvider, SniCertificateSelector) should rebuild.
    /// </summary>
    void SignalReload();
}
