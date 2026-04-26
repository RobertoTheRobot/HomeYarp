namespace HomeYarp.Domain;

public sealed class ClusterDefinition
{
    public string? LoadBalancingPolicy { get; set; }

    public List<DestinationDefinition> Destinations { get; set; } = new();
}
