using System.Security.Cryptography.X509Certificates;
using HomeYarp.Domain;

namespace HomeYarp.Application.RootCa;

public interface IRootCaService
{
    /// <summary>Loads the root CA, generating and persisting a new one on first run. Idempotent.</summary>
    Task<RootCertificateAuthority> EnsureInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>Public root certificate (PEM) — what clients install once into their trust store.</summary>
    Task<string> GetRootCertificatePemAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs a leaf request with the root CA (random serial, authority key identifier added) and returns the
    /// public certificate. <paramref name="notAfter"/> is clamped to the CA's own expiry.
    /// </summary>
    Task<X509Certificate2> SignLeafAsync(
        CertificateRequest request,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        CancellationToken cancellationToken = default);
}
