namespace HomeYarp.Application.Services;

/// <summary>
/// Manually re-applies the on-disk application + certificate state to the running
/// proxy. Add/Update/Delete on the repos no longer auto-fire reload — callers must
/// invoke this service (UI button, REST endpoint, or renewal worker) when they want
/// the live YARP routes and SNI cert map to catch up with disk.
/// </summary>
public interface IRuntimeReloadService
{
    Task ReloadAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>UTC timestamp of the last successful reload, or null if no reload has run yet.</summary>
    DateTimeOffset? LastReloadedAt { get; }
}
