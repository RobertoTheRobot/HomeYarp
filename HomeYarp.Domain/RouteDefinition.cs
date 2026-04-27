namespace HomeYarp.Domain;

public sealed class RouteDefinition
{
    public string? RouteId { get; set; }

    public List<string> Hosts { get; set; } = new();

    public string? Path { get; set; }

    public List<string>? Methods { get; set; }

    public int? Order { get; set; }

    /// <summary>YARP route transforms (advanced). One entry per transform — see YARP docs.</summary>
    public List<RouteTransform>? Transforms { get; set; }
}
