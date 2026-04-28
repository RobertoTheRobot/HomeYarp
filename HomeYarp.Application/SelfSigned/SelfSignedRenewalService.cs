using HomeYarp.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeYarp.Application.SelfSigned;

public sealed class SelfSignedRenewalService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<SelfSignedOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SelfSignedRenewalService> _logger;

    public SelfSignedRenewalService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<SelfSignedOptions> options,
        TimeProvider timeProvider,
        ILogger<SelfSignedRenewalService>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<SelfSignedRenewalService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogInformation("Self-signed renewal worker is disabled (HomeYarp:SelfSigned:Enabled = false).");
            return;
        }

        _logger.LogInformation(
            "Self-signed renewal worker starting; startupDelay={StartupDelay} interval={Interval} renewBefore={RenewBefore}",
            options.StartupDelay,
            options.RenewalInterval,
            options.RenewBefore);

        try
        {
            await Task.Delay(options.StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RenewDueCertificatesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Self-signed renewal tick failed.");
            }

            try
            {
                await Task.Delay(_options.CurrentValue.RenewalInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        _logger.LogInformation("Self-signed renewal worker stopping");
    }

    private async Task RenewDueCertificatesAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICertificateRepository>();
        var selfSigned = scope.ServiceProvider.GetRequiredService<ISelfSignedCertificateService>();
        var reload = scope.ServiceProvider.GetRequiredService<Services.IRuntimeReloadService>();

        var now = _timeProvider.GetUtcNow();
        var threshold = options.RenewBefore;

        var certs = await repo.GetAllAsync(cancellationToken);
        var due = certs.Where(c => c.SelfSigned is not null && c.NotAfter - now < threshold).ToList();

        if (due.Count == 0)
        {
            _logger.LogDebug("Self-signed renewal tick: no certificates due (now={Now:o}, threshold={Threshold}).", now, threshold);
            return;
        }

        _logger.LogInformation("Self-signed renewal tick: {Count} certificate(s) due for renewal.", due.Count);

        var renewedAny = false;
        foreach (var cert in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _logger.LogInformation(
                    "Self-signed renewal: renewing '{Name}' ({Id}); current expiry {NotAfter:o}",
                    cert.Name,
                    cert.Id,
                    cert.NotAfter);
                var renewed = await selfSigned.RegenerateAsync(cert.Id, cancellationToken);
                sw.Stop();
                renewedAny = true;
                _logger.LogInformation(
                    "Self-signed renewal: '{Name}' ({Id}) renewed in {ElapsedMs} ms; new expiry {NotAfter:o}",
                    renewed.Name,
                    renewed.Id,
                    sw.ElapsedMilliseconds,
                    renewed.NotAfter);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Self-signed renewal failed for '{Name}' ({Id}) after {ElapsedMs} ms; will retry on next tick.", cert.Name, cert.Id, sw.ElapsedMilliseconds);
            }
        }

        if (renewedAny)
        {
            // Repos no longer auto-fire reload on save — without this the SNI selector
            // would keep serving the old (about-to-expire) cert until the next manual reload.
            await reload.ReloadAsync(progress: null, cancellationToken);
        }
    }
}
