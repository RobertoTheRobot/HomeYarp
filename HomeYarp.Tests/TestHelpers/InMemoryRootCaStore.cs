using HomeYarp.Application.RootCa;

namespace HomeYarp.Tests.TestHelpers;

internal sealed class InMemoryRootCaStore : IRootCaStore
{
    public RootCaMaterial? Material { get; private set; }
    public int SaveCount { get; private set; }

    public Task<RootCaMaterial?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Material);

    public Task SaveAsync(RootCaMaterial material, CancellationToken cancellationToken = default)
    {
        Material = material;
        SaveCount++;
        return Task.CompletedTask;
    }

    public static RootCaService NewService(InMemoryRootCaStore? store = null, RootCaOptions? options = null, TimeProvider? time = null)
    {
        var monitor = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<RootCaOptions>>();
        monitor.CurrentValue.Returns(options ?? new RootCaOptions());
        return new RootCaService(store ?? new InMemoryRootCaStore(), monitor, time ?? TimeProvider.System);
    }
}
