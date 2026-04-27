namespace HomeYarp.Domain;

public sealed class HealthCheckConfiguration
{
    public ActiveHealthCheckConfiguration? Active { get; set; }

    public PassiveHealthCheckConfiguration? Passive { get; set; }
}

public sealed class ActiveHealthCheckConfiguration
{
    public bool? Enabled { get; set; }

    public TimeSpan? Interval { get; set; }

    public TimeSpan? Timeout { get; set; }

    /// <summary>YARP active-health policy name, e.g. "ConsecutiveFailures".</summary>
    public string? Policy { get; set; }

    public string? Path { get; set; }

    public string? Query { get; set; }
}

public sealed class PassiveHealthCheckConfiguration
{
    public bool? Enabled { get; set; }

    /// <summary>YARP passive-health policy name, e.g. "TransportFailureRate".</summary>
    public string? Policy { get; set; }

    public TimeSpan? ReactivationPeriod { get; set; }
}
