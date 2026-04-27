using HomeYarp.Application.SelfSigned;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HomeYarp.Tests.Application.SelfSigned;

public class SelfSignedRenewalServiceTests
{
    private static IOptionsMonitor<SelfSignedOptions> Monitor(SelfSignedOptions options)
    {
        var monitor = Substitute.For<IOptionsMonitor<SelfSignedOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    [Fact]
    public async Task ExecuteAsync_WhenSelfSignedDisabled_ReturnsImmediately()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var time = new FakeTimeProvider();
        var monitor = Monitor(new SelfSignedOptions { Enabled = false });

        var service = new SelfSignedRenewalService(scopeFactory, monitor, time);

        await service.StartAsync(CancellationToken.None);
        // ExecuteTask completes immediately when Enabled=false.
        var executeTask = service.ExecuteTask;
        _ = executeTask.ShouldNotBeNull();
        await executeTask!.WaitAsync(TimeSpan.FromSeconds(2));

        scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task StopAsync_AfterStart_CompletesEvenWhenLoopRunning()
    {
        // We can't mock the BackgroundService internal Task.Delay easily, so this test
        // confirms we cleanly cancel the loop when the host shuts down.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var time = new FakeTimeProvider();
        var options = new SelfSignedOptions
        {
            Enabled = true,
            StartupDelay = TimeSpan.FromHours(1),
            RenewalInterval = TimeSpan.FromHours(1)
        };

        var service = new SelfSignedRenewalService(scopeFactory, Monitor(options), time);

        await service.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(cts.Token);

        cts.IsCancellationRequested.ShouldBeFalse(); // We stopped before the timeout fired.
    }
}
