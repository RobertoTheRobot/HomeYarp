using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Services;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;

namespace HomeYarp.Tests.Application.Services;

public class RuntimeReloadServiceTests
{
    private readonly IApplicationRepository _apps = Substitute.For<IApplicationRepository>();
    private readonly ICertificateRepository _certs = Substitute.For<ICertificateRepository>();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-04-28T12:00:00Z"));

    private RuntimeReloadService Service => new(_apps, _certs, _time);

    [Fact]
    public async Task ReloadAsync_FiresSignalReloadOnBothRepos()
    {
        await Service.ReloadAsync();

        _apps.Received(1).SignalReload();
        _certs.Received(1).SignalReload();
    }

    [Fact]
    public async Task ReloadAsync_RecordsLastReloadedAt()
    {
        var sut = Service;
        sut.LastReloadedAt.ShouldBeNull();

        await sut.ReloadAsync();

        sut.LastReloadedAt.ShouldBe(_time.GetUtcNow());
    }

    [Fact]
    public async Task ReloadAsync_ReportsExpectedSteps()
    {
        var reported = new List<string>();
        var progress = new SynchronousProgress<string>(reported.Add);

        await Service.ReloadAsync(progress);

        reported.ShouldNotBeEmpty();
        reported.ShouldContain(s => s.Contains("YARP", StringComparison.OrdinalIgnoreCase));
        reported.ShouldContain(s => s.Contains("SNI", StringComparison.OrdinalIgnoreCase));
        reported.ShouldContain(s => s.StartsWith("Done", StringComparison.OrdinalIgnoreCase));
    }
}
