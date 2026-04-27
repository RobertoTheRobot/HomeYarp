using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Acme;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Domain;
using Microsoft.Extensions.Options;

namespace HomeYarp.Application.Services;

public sealed class ApplicationService : IApplicationService
{
    private readonly IApplicationRepository _repository;
    private readonly ICertificateRepository _certificates;
    private readonly ISelfSignedCertificateService _selfSigned;
    private readonly IAcmeService _acme;
    private readonly IOptionsMonitor<AcmeOptions> _acmeOptions;

    public ApplicationService(
        IApplicationRepository repository,
        ICertificateRepository certificates,
        ISelfSignedCertificateService selfSigned,
        IAcmeService acme,
        IOptionsMonitor<AcmeOptions> acmeOptions)
    {
        _repository = repository;
        _certificates = certificates;
        _selfSigned = selfSigned;
        _acme = acme;
        _acmeOptions = acmeOptions;
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

        await EnsureAutoManagedCertificateAsync(application, previous: null, cancellationToken);

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

        await EnsureAutoManagedCertificateAsync(application, previous: existing, cancellationToken);

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

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        var removed = await _repository.DeleteAsync(id, cancellationToken);
        if (removed && existing.Tls.Source != TlsCertificateSource.Manual && existing.Tls.CertificateId is { } certId)
        {
            await _certificates.DeleteAsync(certId, cancellationToken);
        }
        return removed;
    }

    private async Task EnsureAutoManagedCertificateAsync(
        Domain.Application incoming,
        Domain.Application? previous,
        CancellationToken cancellationToken)
    {
        var prevSource = previous?.Tls.Source ?? TlsCertificateSource.Manual;
        var prevCertId = previous?.Tls.CertificateId;
        var newSource = incoming.Tls.Source;

        // Source switch (Internal↔External, or auto→Manual): drop the previously auto-managed cert.
        if (prevSource != TlsCertificateSource.Manual && prevSource != newSource && prevCertId is { } oldId)
        {
            await _certificates.DeleteAsync(oldId, cancellationToken);
        }

        if (newSource == TlsCertificateSource.Manual)
        {
            // User-managed: nothing to provision. CertificateId is whatever the request set.
            return;
        }

        var hostnames = CollectHostnames(incoming);

        if (newSource == TlsCertificateSource.External)
        {
            AcmeOptionsValidator.EnsureConfigured(_acmeOptions.CurrentValue);
        }

        // Reuse-if-unchanged: if we already have an auto-cert of the same source and the hostnames match, leave it alone.
        if (newSource == prevSource && prevCertId is { } existingId)
        {
            var existingCert = await _certificates.GetByIdAsync(existingId, cancellationToken);
            if (existingCert is not null && SameHostnames(existingCert, newSource, hostnames))
            {
                incoming.Tls.CertificateId = existingId;
                return;
            }

            if (existingCert is not null)
            {
                if (newSource == TlsCertificateSource.Internal)
                {
                    var regen = await _selfSigned.RegenerateAsync(existingId, hostnames, cancellationToken);
                    incoming.Tls.CertificateId = regen.Id;
                    return;
                }

                // External: hostname re-issue not supported in v1.
                throw new InvalidOperationException(
                    "Changing hostnames on an External-managed application is not supported. " +
                    "Delete the application and recreate it, or switch to Manual and pick a different certificate.");
            }
            // Cert was deleted out from under us; fall through and create fresh.
        }

        var certName = BuildCertName(incoming.Name, newSource);
        var friendly = BuildFriendlyName(incoming, newSource);

        var created = newSource == TlsCertificateSource.Internal
            ? await _selfSigned.IssueAsync(certName, friendly, hostnames, CertificateKeyType.Ec256, validityDays: 365, cancellationToken)
            : await _acme.IssueAsync(certName, friendly, hostnames, cancellationToken);

        incoming.Tls.CertificateId = created.Id;
    }

    private static List<string> CollectHostnames(Domain.Application app)
    {
        return app.Routes
            .SelectMany(r => r.Hosts ?? Enumerable.Empty<string>())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SameHostnames(Certificate cert, TlsCertificateSource source, IReadOnlyList<string> incoming)
    {
        var current = source switch
        {
            TlsCertificateSource.Internal => cert.SelfSigned?.Hostnames ?? new List<string>(),
            TlsCertificateSource.External => cert.Acme?.Hostnames ?? new List<string>(),
            _ => new List<string>()
        };
        if (current.Count != incoming.Count) return false;
        var a = current.OrderBy(h => h, StringComparer.OrdinalIgnoreCase);
        var b = incoming.OrderBy(h => h, StringComparer.OrdinalIgnoreCase);
        return a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildCertName(string appName, TlsCertificateSource source)
    {
        var suffix = source == TlsCertificateSource.Internal ? "internal" : "external";
        return $"{appName}-{suffix}";
    }

    private static string? BuildFriendlyName(Domain.Application app, TlsCertificateSource source)
    {
        var label = source == TlsCertificateSource.Internal ? "internal" : "Let's Encrypt";
        var display = string.IsNullOrWhiteSpace(app.DisplayName) ? app.Name : app.DisplayName;
        return $"{display} ({label})";
    }

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

        ValidateTls(application);
    }

    private static void ValidateTls(Domain.Application application)
    {
        var tls = application.Tls;

        if (tls.Source != TlsCertificateSource.Manual && tls.Mode != TlsMode.Offload)
        {
            throw new ArgumentException(
                "Internal and External certificate sources require TLS mode 'Offload'.",
                nameof(application));
        }

        if (tls.Mode == TlsMode.Offload && tls.Source == TlsCertificateSource.Manual && tls.CertificateId is null)
        {
            throw new ArgumentException(
                "TLS Offload with Manual source requires a CertificateId. Pick an existing certificate or switch to Internal/External.",
                nameof(application));
        }

        if (tls.Source != TlsCertificateSource.Manual)
        {
            var hosts = CollectHostnames(application);
            if (hosts.Count == 0)
            {
                throw new ArgumentException(
                    "Internal and External certificate sources require at least one route hostname.",
                    nameof(application));
            }

            if (tls.Source == TlsCertificateSource.External)
            {
                foreach (var host in hosts)
                {
                    if (host.StartsWith("*.", StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            $"Wildcard hostname '{host}' cannot be issued by Let's Encrypt over HTTP-01. Use the Internal source for wildcards.",
                            nameof(application));
                    }
                }
            }
        }
    }
}
