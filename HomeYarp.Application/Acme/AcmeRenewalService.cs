using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeYarp.Application.Acme;

public sealed class AcmeRenewalService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AcmeOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AcmeRenewalService> _logger;

    public AcmeRenewalService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AcmeOptions> options,
        TimeProvider timeProvider,
        ILogger<AcmeRenewalService>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<AcmeRenewalService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogInformation("ACME renewal worker is disabled (HomeYarp:Acme:Enabled = false).");
            return;
        }

        _logger.LogInformation(
            "ACME renewal worker starting; startupDelay={StartupDelay} interval={Interval} renewBefore={RenewBefore} directory={Directory}",
            options.StartupDelay,
            options.RenewalInterval,
            options.RenewBefore,
            options.DirectoryUrl);

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
                _logger.LogError(ex, "ACME renewal tick failed.");
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

        _logger.LogInformation("ACME renewal worker stopping");
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
        var acme = scope.ServiceProvider.GetRequiredService<IAcmeService>();

        var now = _timeProvider.GetUtcNow();
        var threshold = options.RenewBefore;

        var certs = await repo.GetAllAsync(cancellationToken);
        var due = certs.Where(c => c.Acme is not null && c.NotAfter - now < threshold).ToList();

        if (due.Count == 0)
        {
            _logger.LogDebug("ACME renewal tick: no certificates due (now={Now:o}, threshold={Threshold}).", now, threshold);
            return;
        }

        _logger.LogInformation("ACME renewal tick: {Count} certificate(s) due for renewal.", due.Count);

        foreach (var cert in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _logger.LogInformation(
                    "ACME renewal: renewing '{Name}' ({Id}); current expiry {NotAfter:o}",
                    cert.Name,
                    cert.Id,
                    cert.NotAfter);
                var renewed = await acme.RenewAsync(cert.Id, cancellationToken);
                sw.Stop();
                _logger.LogInformation(
                    "ACME renewal: '{Name}' ({Id}) renewed in {ElapsedMs} ms; new expiry {NotAfter:o}",
                    renewed.Name,
                    renewed.Id,
                    sw.ElapsedMilliseconds,
                    renewed.NotAfter);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "ACME renewal failed for '{Name}' ({Id}) after {ElapsedMs} ms; will retry on next tick.", cert.Name, cert.Id, sw.ElapsedMilliseconds);
            }
        }
    }
}
