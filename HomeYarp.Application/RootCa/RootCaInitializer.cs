using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HomeYarp.Application.RootCa;

/// <summary>Creates the root CA at startup so it (and its download endpoint) exist before any cert is issued.</summary>
public sealed class RootCaInitializer : IHostedService
{
    private readonly IRootCaService _rootCa;

    public RootCaInitializer(IRootCaService rootCa) => _rootCa = rootCa;

    public async Task StartAsync(CancellationToken cancellationToken)
        => await _rootCa.EnsureInitializedAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
