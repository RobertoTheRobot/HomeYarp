namespace HomeYarp.Domain;

public sealed class Certificate
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    public string? FriendlyName { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    public string Thumbprint { get; set; } = string.Empty;

    public DateTimeOffset NotBefore { get; set; }

    public DateTimeOffset NotAfter { get; set; }

    public List<string> SubjectAlternativeNames { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AcmeMetadata? Acme { get; set; }
}
