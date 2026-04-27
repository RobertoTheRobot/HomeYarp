namespace HomeYarp.Domain;

public enum CertificateKeyType
{
    Ec256 = 0,
    Rsa2048 = 1
}

public sealed class SelfSignedMetadata
{
    public List<string> Hostnames { get; set; } = new();

    public CertificateKeyType KeyType { get; set; } = CertificateKeyType.Ec256;

    public int ValidityDays { get; set; } = 365;

    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RegeneratedAt { get; set; }
}
