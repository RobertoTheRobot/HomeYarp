using HomeYarp.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeYarp.Application.Services;

public sealed class RuntimeReloadService : IRuntimeReloadService
{
    private readonly IApplicationRepository _apps;
    private readonly ICertificateRepository _certs;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RuntimeReloadService> _logger;
    private readonly object _gate = new();

    public RuntimeReloadService(
        IApplicationRepository apps,
        ICertificateRepository certs,
        TimeProvider timeProvider,
        ILogger<RuntimeReloadService>? logger = null)
    {
        _apps = apps;
        _certs = certs;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<RuntimeReloadService>.Instance;
    }

    public DateTimeOffset? LastReloadedAt { get; private set; }

    public Task ReloadAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        // Serialize concurrent reloads — SignalReload's CTS swap is already thread-safe,
        // but back-to-back calls would do redundant work and confuse progress logs.
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Manual runtime reload requested");

            progress?.Report("Rebuilding YARP route + cluster table");
            // Triggers HomeYarpConfigProvider.ReloadConfig AND SniCertificateSelector.Reload
            // synchronously via ChangeToken.OnChange callbacks. Both consumers wrap their
            // body in try/catch, so a failure in one doesn't block the other.
            _apps.SignalReload();

            progress?.Report("Rebuilding SNI certificate map");
            // SniCertificateSelector also subscribes to the cert repo. Triggering the cert
            // repo as well covers the cert-only edit case (e.g. re-uploading PEM material
            // for an existing certId without touching apps).
            _certs.SignalReload();

            sw.Stop();
            LastReloadedAt = _timeProvider.GetUtcNow();
            progress?.Report($"Done in {sw.ElapsedMilliseconds} ms");
            _logger.LogInformation("Manual runtime reload complete in {ElapsedMs} ms", sw.ElapsedMilliseconds);
            return Task.CompletedTask;
        }
    }
}
