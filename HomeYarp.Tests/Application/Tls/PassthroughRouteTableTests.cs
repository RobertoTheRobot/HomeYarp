using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Tls;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Primitives;

namespace HomeYarp.Tests.Application.Tls;

public class PassthroughRouteTableTests
{
    private readonly IApplicationRepository _apps = Substitute.For<IApplicationRepository>();
    private CancellationTokenSource _reloadCts = new();

    public PassthroughRouteTableTests()
    {
        _apps.GetReloadToken().Returns(_ => new CancellationChangeToken(_reloadCts.Token));
        _apps.WithApps();
    }

    private void FireReload()
    {
        var firing = _reloadCts;
        _reloadCts = new CancellationTokenSource();
        _apps.GetReloadToken().Returns(_ => new CancellationChangeToken(_reloadCts.Token));
        firing.Cancel();
    }

    [Fact]
    public void TryResolve_ExactHost_ReturnsApp()
    {
        var app = ApplicationFactory.Create(
            name: "ha",
            tlsMode: TlsMode.Passthrough,
            routeHosts: new[] { "ha.home.lan" });
        _apps.WithApps(app);

        using var table = new PassthroughRouteTable(_apps);

        table.TryResolve("ha.home.lan", out var resolved).ShouldBeTrue();
        resolved.Name.ShouldBe("ha");
    }

    [Fact]
    public void TryResolve_ExactHost_IsCaseInsensitive()
    {
        var app = ApplicationFactory.Create(
            name: "ha",
            tlsMode: TlsMode.Passthrough,
            routeHosts: new[] { "HA.home.lan" });
        _apps.WithApps(app);

        using var table = new PassthroughRouteTable(_apps);

        table.TryResolve("ha.home.lan", out var resolved).ShouldBeTrue();
        resolved.Name.ShouldBe("ha");
    }

    [Fact]
    public void TryResolve_WildcardHost_MatchesSubdomain()
    {
        var app = ApplicationFactory.Create(
            name: "wild",
            tlsMode: TlsMode.Passthrough,
            routeHosts: new[] { "*.home.lan" });
        _apps.WithApps(app);

        using var table = new PassthroughRouteTable(_apps);

        table.TryResolve("anything.home.lan", out var resolved).ShouldBeTrue();
        resolved.Name.ShouldBe("wild");
    }

    [Fact]
    public void TryResolve_DisabledApp_NotResolved()
    {
        var app = ApplicationFactory.Create(
            name: "off",
            enabled: false,
            tlsMode: TlsMode.Passthrough,
            routeHosts: new[] { "off.home.lan" });
        _apps.WithApps(app);

        using var table = new PassthroughRouteTable(_apps);

        table.TryResolve("off.home.lan", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryResolve_NonPassthroughApp_NotResolved()
    {
        var app = ApplicationFactory.Create(
            name: "offload-only",
            tlsMode: TlsMode.Offload,
            routeHosts: new[] { "x.home.lan" });
        _apps.WithApps(app);

        using var table = new PassthroughRouteTable(_apps);

        table.TryResolve("x.home.lan", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryResolve_UnknownHost_ReturnsFalse()
    {
        using var table = new PassthroughRouteTable(_apps);

        table.TryResolve("nope.home.lan", out _).ShouldBeFalse();
    }

    [Fact]
    public void Rebuild_AfterRepoSignalReload_PicksUpNewApp()
    {
        using var table = new PassthroughRouteTable(_apps);
        table.TryResolve("ha.home.lan", out _).ShouldBeFalse();

        var app = ApplicationFactory.Create(
            name: "ha",
            tlsMode: TlsMode.Passthrough,
            routeHosts: new[] { "ha.home.lan" });
        _apps.WithApps(app);
        FireReload();

        table.TryResolve("ha.home.lan", out var resolved).ShouldBeTrue();
        resolved.Name.ShouldBe("ha");
    }

    [Fact]
    public void Rebuild_AfterRepoSignalReload_DropsRemovedApp()
    {
        var app = ApplicationFactory.Create(
            name: "ha",
            tlsMode: TlsMode.Passthrough,
            routeHosts: new[] { "ha.home.lan" });
        _apps.WithApps(app);

        using var table = new PassthroughRouteTable(_apps);
        table.TryResolve("ha.home.lan", out _).ShouldBeTrue();

        _apps.WithApps();
        FireReload();

        table.TryResolve("ha.home.lan", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryResolve_MultipleAppsOneSni_ReturnsExactOverWildcard()
    {
        var exact = ApplicationFactory.Create(
            name: "exact",
            tlsMode: TlsMode.Passthrough,
            routeHosts: new[] { "ha.home.lan" });
        var wild = ApplicationFactory.Create(
            name: "wild",
            tlsMode: TlsMode.Passthrough,
            routeHosts: new[] { "*.home.lan" });
        _apps.WithApps(exact, wild);

        using var table = new PassthroughRouteTable(_apps);

        table.TryResolve("ha.home.lan", out var resolved).ShouldBeTrue();
        resolved.Name.ShouldBe("exact");
    }
}
