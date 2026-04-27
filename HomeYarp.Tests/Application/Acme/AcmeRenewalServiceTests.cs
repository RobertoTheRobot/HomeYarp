using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Acme;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HomeYarp.Tests.Application.Acme;

public class AcmeRenewalServiceTests
{
    private static IOptionsMonitor<AcmeOptions> Monitor(AcmeOptions options)
    {
        var monitor = Substitute.For<IOptionsMonitor<AcmeOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    [Fact]
    public async Task ExecuteAsync_WhenAcmeDisabled_ReturnsImmediately()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var time = new FakeTimeProvider();
        var monitor = Monitor(new AcmeOptions { Enabled = false });

        var service = new AcmeRenewalService(scopeFactory, monitor, time);

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
        var options = new AcmeOptions
        {
            Enabled = true,
            AgreeToTermsOfService = true,
            AccountEmail = "a@b.c",
            DirectoryUrl = "https://example.test/directory",
            StartupDelay = TimeSpan.FromHours(1),
            RenewalInterval = TimeSpan.FromHours(1)
        };

        var service = new AcmeRenewalService(scopeFactory, Monitor(options), time);

        await service.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(cts.Token);

        cts.IsCancellationRequested.ShouldBeFalse(); // We stopped before the timeout fired.
    }
}
