using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Proxy;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Primitives;

namespace HomeYarp.Tests.Application.Proxy;

public class HomeYarpConfigProviderTests
{
    private readonly IApplicationRepository _repo = Substitute.For<IApplicationRepository>();

    public HomeYarpConfigProviderTests()
    {
        var token = Substitute.For<IChangeToken>();
        token.HasChanged.Returns(false);
        token.RegisterChangeCallback(Arg.Any<Action<object?>>(), Arg.Any<object?>())
             .Returns(new DisposeStub());
        _repo.GetReloadToken().Returns(token);
    }

    private sealed class DisposeStub : IDisposable { public void Dispose() { } }

    [Fact]
    public void GetConfig_WithSingleEnabledApp_BuildsRouteAndCluster()
    {
        var app = ApplicationFactory.Create(name: "grafana", routeHosts: new[] { "grafana.lan" });
        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { app });

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        config.Clusters.ShouldHaveSingleItem();
        config.Clusters[0].ClusterId.ShouldBe("grafana-cluster");
        config.Clusters[0].Destinations.ShouldNotBeNull();
        config.Clusters[0].Destinations!.ContainsKey("primary").ShouldBeTrue();

        config.Routes.ShouldHaveSingleItem();
        config.Routes[0].RouteId.ShouldBe("grafana-route-0");
        config.Routes[0].ClusterId.ShouldBe("grafana-cluster");
        config.Routes[0].Match.Hosts.ShouldNotBeNull();
        config.Routes[0].Match.Hosts!.ShouldContain("grafana.lan");
    }

    [Fact]
    public void GetConfig_WithDisabledApp_OmitsItFromConfig()
    {
        var app = ApplicationFactory.Create(name: "off", enabled: false, routeHosts: new[] { "off.lan" });
        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { app });

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        config.Routes.ShouldBeEmpty();
        config.Clusters.ShouldBeEmpty();
    }

    [Fact]
    public void GetConfig_WithNoDestinations_OmitsApp()
    {
        var app = ApplicationFactory.Create(name: "empty", routeHosts: new[] { "x.lan" });
        app.Cluster.Destinations.Clear();
        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { app });

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        config.Routes.ShouldBeEmpty();
        config.Clusters.ShouldBeEmpty();
    }

    [Fact]
    public void GetConfig_WithCustomRouteId_HonorsIt()
    {
        var app = ApplicationFactory.Create(name: "custom", routeHosts: new[] { "x.lan" });
        app.Routes[0].RouteId = "my-route";
        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { app });

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        config.Routes[0].RouteId.ShouldBe("my-route");
    }

    [Fact]
    public void GetConfig_WithEmptyHostsList_SetsHostsToNull()
    {
        var app = ApplicationFactory.Create(name: "no-hosts", routeHosts: Array.Empty<string>());
        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { app });

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        config.Routes.ShouldHaveSingleItem();
        config.Routes[0].Match.Hosts.ShouldBeNull();
    }
}
