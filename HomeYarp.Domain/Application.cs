namespace HomeYarp.Domain;

public sealed class Application
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    public List<RouteDefinition> Routes { get; set; } = new();

    public ClusterDefinition Cluster { get; set; } = new();

    public TlsConfiguration Tls { get; set; } = new();

    public string? AuthorizationPolicy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
