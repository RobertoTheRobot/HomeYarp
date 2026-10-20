namespace HomeYarp.Domain;

/// <summary>
/// Public metadata + certificate of this instance's private root CA. The private key is
/// deliberately not part of this type; it lives only in the storage layer and the signing service.
/// </summary>
public sealed record RootCertificateAuthority(
    string CertificatePem,
    string Subject,
    string Thumbprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter);
