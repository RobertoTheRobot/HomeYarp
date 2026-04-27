using System.Security.Cryptography.X509Certificates;
using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeYarp.Application.Services;

public sealed class CertificateService : ICertificateService
{
    private readonly ICertificateRepository _repository;
    private readonly ILogger<CertificateService> _logger;

    public CertificateService(ICertificateRepository repository, ILogger<CertificateService>? logger = null)
    {
        _repository = repository;
        _logger = logger ?? NullLogger<CertificateService>.Instance;
    }

    public Task<IReadOnlyList<Certificate>> ListAsync(CancellationToken cancellationToken = default)
        => _repository.GetAllAsync(cancellationToken);

    public Task<Certificate?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => _repository.GetByIdAsync(id, cancellationToken);

    public async Task<Certificate> UploadAsync(string name, string? friendlyName, CertificateMaterial material, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Certificate name is required.", nameof(name));
        }

        var existing = await _repository.GetByNameAsync(name, cancellationToken);
        if (existing is not null)
        {
            _logger.LogWarning("Certificate upload rejected: name '{CertName}' already exists ({ExistingId})", name, existing.Id);
            throw new InvalidOperationException($"A certificate named '{name}' already exists.");
        }

        X509Certificate2 parsed;
        try
        {
            parsed = X509Certificate2.CreateFromPem(material.CertificatePem, material.PrivateKeyPem);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Certificate upload rejected for '{CertName}': invalid PEM material", name);
            throw new ArgumentException($"Invalid PEM certificate or key: {ex.Message}", ex);
        }

        using (parsed)
        {
            var sans = parsed.Extensions
                .OfType<X509SubjectAlternativeNameExtension>()
                .SelectMany(e => e.EnumerateDnsNames())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var cert = new Certificate
            {
                Name = name,
                FriendlyName = friendlyName,
                Subject = parsed.Subject,
                Issuer = parsed.Issuer,
                Thumbprint = parsed.Thumbprint,
                NotBefore = new DateTimeOffset(parsed.NotBefore.ToUniversalTime(), TimeSpan.Zero),
                NotAfter = new DateTimeOffset(parsed.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                SubjectAlternativeNames = sans,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await _repository.SaveAsync(cert, material, cancellationToken);

            _logger.LogInformation(
                "Certificate uploaded: '{CertName}' ({CertId}) subject='{Subject}' issuer='{Issuer}' notAfter={NotAfter:o} sans=[{Sans}]",
                cert.Name,
                cert.Id,
                cert.Subject,
                cert.Issuer,
                cert.NotAfter,
                string.Join(",", sans));
            return cert;
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetByIdAsync(id, cancellationToken);
        var removed = await _repository.DeleteAsync(id, cancellationToken);
        if (removed && existing is not null)
        {
            _logger.LogInformation("Certificate deleted: '{CertName}' ({CertId})", existing.Name, existing.Id);
        }
        else if (!removed)
        {
            _logger.LogDebug("Certificate delete: id {CertId} not found", id);
        }
        return removed;
    }
}
