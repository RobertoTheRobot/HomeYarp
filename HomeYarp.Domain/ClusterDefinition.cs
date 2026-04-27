namespace HomeYarp.Domain;

public sealed class ClusterDefinition
{
    public string? LoadBalancingPolicy { get; set; }

    public List<DestinationDefinition> Destinations { get; set; } = new();

    /// <summary>YARP active/passive health-check configuration (advanced).</summary>
    public HealthCheckConfiguration? HealthCheck { get; set; }

    /// <summary>YARP HTTP request options (timeouts, version) for backend calls (advanced).</summary>
    public HttpRequestConfiguration? HttpRequest { get; set; }
}
