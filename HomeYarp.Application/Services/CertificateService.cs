using System.Security.Cryptography.X509Certificates;
using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;

namespace HomeYarp.Application.Services;

public sealed class CertificateService : ICertificateService
{
    private readonly ICertificateRepository _repository;

    public CertificateService(ICertificateRepository repository)
    {
        _repository = repository;
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
            throw new InvalidOperationException($"A certificate named '{name}' already exists.");
        }

        X509Certificate2 parsed;
        try
        {
            parsed = X509Certificate2.CreateFromPem(material.CertificatePem, material.PrivateKeyPem);
        }
        catch (Exception ex)
        {
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
            return cert;
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => _repository.DeleteAsync(id, cancellationToken);
}
