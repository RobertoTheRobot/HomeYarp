using HomeYarp.Domain;

namespace HomeYarp.Application.Services;

public interface IApplicationService
{
    Task<IReadOnlyList<Domain.Application>> ListAsync(CancellationToken cancellationToken = default);

    Task<Domain.Application?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Domain.Application> CreateAsync(Domain.Application application, CancellationToken cancellationToken = default);

    Task<Domain.Application> UpdateAsync(Guid id, Domain.Application application, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
