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
}
