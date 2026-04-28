namespace HomeYarp.WebServer.Components.Shared;

/// <summary>One line in the live save/issue log shown by <see cref="ProgressLog"/>.</summary>
public sealed record ProgressStep(DateTimeOffset Timestamp, string Message);
