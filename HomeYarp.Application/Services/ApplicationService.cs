using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;

namespace HomeYarp.Application.Services;

public sealed class ApplicationService : IApplicationService
{
    private readonly IApplicationRepository _repository;

    public ApplicationService(IApplicationRepository repository)
    {
        _repository = repository;
    }

    public Task<IReadOnlyList<Domain.Application>> ListAsync(CancellationToken cancellationToken = default)
        => _repository.GetAllAsync(cancellationToken);

    public Task<Domain.Application?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => _repository.GetByIdAsync(id, cancellationToken);

    public async Task<Domain.Application> CreateAsync(Domain.Application application, CancellationToken cancellationToken = default)
    {
        Validate(application);

        var existing = await _repository.GetByNameAsync(application.Name, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"An application named '{application.Name}' already exists.");
        }

        application.CreatedAt = DateTimeOffset.UtcNow;
        application.UpdatedAt = application.CreatedAt;
        await _repository.AddAsync(application, cancellationToken);
        return application;
    }

    public async Task<Domain.Application> UpdateAsync(Guid id, Domain.Application application, CancellationToken cancellationToken = default)
    {
        Validate(application);

        var existing = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Application '{id}' not found.");

        var byName = await _repository.GetByNameAsync(application.Name, cancellationToken);
        if (byName is not null && byName.Id != id)
        {
            throw new InvalidOperationException($"Another application named '{application.Name}' already exists.");
        }

        existing.Name = application.Name;
        existing.DisplayName = application.DisplayName;
        existing.Description = application.Description;
        existing.Enabled = application.Enabled;
        existing.Routes = application.Routes;
        existing.Cluster = application.Cluster;
        existing.Tls = application.Tls;
        existing.AuthorizationPolicy = application.AuthorizationPolicy;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await _repository.UpdateAsync(existing, cancellationToken);
        return existing;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => _repository.DeleteAsync(id, cancellationToken);

    private static void Validate(Domain.Application application)
    {
        if (string.IsNullOrWhiteSpace(application.Name))
        {
            throw new ArgumentException("Application name is required.", nameof(application));
        }

        if (application.Cluster is null || application.Cluster.Destinations.Count == 0)
        {
            throw new ArgumentException("At least one destination is required.", nameof(application));
        }

        foreach (var destination in application.Cluster.Destinations)
        {
            if (string.IsNullOrWhiteSpace(destination.Name))
            {
                throw new ArgumentException("Destination name is required.", nameof(application));
            }

            if (!Uri.TryCreate(destination.Address, UriKind.Absolute, out _))
            {
                throw new ArgumentException($"Destination '{destination.Name}' has an invalid address: '{destination.Address}'.", nameof(application));
            }
        }
    }
}
