namespace HomeYarp.Domain;

public sealed class HttpRequestConfiguration
{
    public TimeSpan? ActivityTimeout { get; set; }

    /// <summary>"1.1" | "2" | "3".</summary>
    public string? Version { get; set; }

    /// <summary>"RequestVersionOrLower" | "RequestVersionOrHigher" | "RequestVersionExact".</summary>
    public string? VersionPolicy { get; set; }

    public bool? AllowResponseBuffering { get; set; }
}
