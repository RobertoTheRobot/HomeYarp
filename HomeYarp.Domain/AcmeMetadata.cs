namespace HomeYarp.Domain;

public enum AcmeKeyType
{
    Ec256 = 0,
    Rsa2048 = 1
}

public sealed class AcmeMetadata
{
    public List<string> Hostnames { get; set; } = new();

    public string AccountEmail { get; set; } = string.Empty;

    public string DirectoryUrl { get; set; } = string.Empty;

    public AcmeKeyType KeyType { get; set; } = AcmeKeyType.Ec256;

    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RenewedAt { get; set; }
}
