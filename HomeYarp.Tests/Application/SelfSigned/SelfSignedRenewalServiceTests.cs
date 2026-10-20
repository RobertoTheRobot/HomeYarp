using HomeYarp.Application.RootCa;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Application.Services;
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

    private static IOptionsMonitor<RootCaOptions> RootMonitor(RootCaOptions options)
    {
        var monitor = Substitute.For<IOptionsMonitor<RootCaOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    [Fact]
    public async Task ExecuteAsync_WhenSelfSignedDisabled_ReturnsImmediately()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var time = new FakeTimeProvider();
        var monitor = Monitor(new SelfSignedOptions { Enabled = false });

        var service = new SelfSignedRenewalService(scopeFactory, monitor, RootMonitor(new RootCaOptions()), time);

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

        var service = new SelfSignedRenewalService(scopeFactory, Monitor(options), RootMonitor(new RootCaOptions()), time);

        await service.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(cts.Token);

        cts.IsCancellationRequested.ShouldBeFalse(); // We stopped before the timeout fired.
    }

    private static (IServiceScopeFactory Factory, ISelfSignedCertificateService SelfSigned, IRuntimeReloadService Reload) Provider(
        IEnumerable<Certificate> certs, string rootSubject)
    {
        var repo = Substitute.For<HomeYarp.Application.Abstractions.ICertificateRepository>();
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(certs.ToList());
        var selfSigned = Substitute.For<ISelfSignedCertificateService>();
        selfSigned.RegenerateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new Certificate { Id = ci.Arg<Guid>(), Name = "renewed", NotAfter = DateTimeOffset.UtcNow.AddDays(365) }));
        var reload = Substitute.For<IRuntimeReloadService>();
        var rootCa = Substitute.For<IRootCaService>();
        rootCa.EnsureInitializedAsync(Arg.Any<CancellationToken>())
            .Returns(new RootCertificateAuthority("pem", rootSubject, "THUMB", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(15)));

        var services = new ServiceCollection();
        services.AddSingleton(repo);
        services.AddSingleton(selfSigned);
        services.AddSingleton(reload);
        services.AddSingleton(rootCa);
        return (services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), selfSigned, reload);
    }

    private static async Task<int> RunOneTickAsync(IServiceScopeFactory factory, bool migrate)
    {
        var options = new SelfSignedOptions { Enabled = true, StartupDelay = TimeSpan.Zero, RenewalInterval = TimeSpan.FromHours(1) };
        var service = new SelfSignedRenewalService(factory, Monitor(options), RootMonitor(new RootCaOptions { MigrateLegacyCertificates = migrate }), TimeProvider.System);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(500); // one tick runs immediately (startup delay is zero); the next is an hour away
        await service.StopAsync(CancellationToken.None);
        return 0;
    }

    [Fact]
    public async Task Tick_WhenMigrationEnabled_RegeneratesLegacySelfSignedCertsButNotCaSignedOnes()
    {
        var legacy = HomeYarp.Tests.TestHelpers.ApplicationFactory.CreateSelfSignedCert("legacy", new[] { "old.home.lan" }); // issuer == subject
        var signed = HomeYarp.Tests.TestHelpers.ApplicationFactory.CreateSelfSignedCert("signed", new[] { "new.home.lan" });
        signed.Issuer = "CN=HomeYarp Local Root CA";
        var (factory, selfSigned, reload) = Provider(new[] { legacy, signed }, "CN=HomeYarp Local Root CA");

        await RunOneTickAsync(factory, migrate: true);

        await selfSigned.Received(1).RegenerateAsync(legacy.Id, Arg.Any<CancellationToken>());
        await selfSigned.DidNotReceive().RegenerateAsync(signed.Id, Arg.Any<CancellationToken>());
        await reload.Received().ReloadAsync(Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tick_WhenMigrationDisabled_LeavesLegacyCertsAlone()
    {
        var legacy = HomeYarp.Tests.TestHelpers.ApplicationFactory.CreateSelfSignedCert("legacy", new[] { "old.home.lan" });
        var (factory, selfSigned, _) = Provider(new[] { legacy }, "CN=HomeYarp Local Root CA");

        await RunOneTickAsync(factory, migrate: false);

        await selfSigned.DidNotReceive().RegenerateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
