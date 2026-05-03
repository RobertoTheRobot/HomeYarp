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
        _repo.WithApps(app);

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
        _repo.WithApps(app);

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
        _repo.WithApps(app);

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
        _repo.WithApps(app);

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        config.Routes[0].RouteId.ShouldBe("my-route");
    }

    [Fact]
    public void GetConfig_WithRouteTransforms_PropagatesToYarpRouteConfig()
    {
        var app = ApplicationFactory.Create(name: "transformed", routeHosts: new[] { "x.lan" });
        app.Routes[0].Transforms = new List<RouteTransform>
        {
            new() { ["PathSet"] = "/api/v2" },
            new() { ["RequestHeader"] = "X-Forwarded-User", ["Set"] = "anonymous" }
        };
        _repo.WithApps(app);

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        config.Routes[0].Transforms.ShouldNotBeNull();
        config.Routes[0].Transforms!.Count.ShouldBe(2);
        config.Routes[0].Transforms![0]["PathSet"].ShouldBe("/api/v2");
        config.Routes[0].Transforms![1]["RequestHeader"].ShouldBe("X-Forwarded-User");
    }

    [Fact]
    public void GetConfig_WithoutTransforms_LeavesTransformsNull()
    {
        var app = ApplicationFactory.Create(name: "plain", routeHosts: new[] { "x.lan" });
        _repo.WithApps(app);

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        config.Routes[0].Transforms.ShouldBeNull();
    }

    [Fact]
    public void GetConfig_WithActiveAndPassiveHealthCheck_PropagatesToYarpClusterConfig()
    {
        var app = ApplicationFactory.Create(name: "hc", routeHosts: new[] { "x.lan" });
        app.Cluster.HealthCheck = new HealthCheckConfiguration
        {
            Active = new ActiveHealthCheckConfiguration
            {
                Enabled = true,
                Interval = TimeSpan.FromSeconds(30),
                Timeout = TimeSpan.FromSeconds(5),
                Policy = "ConsecutiveFailures",
                Path = "/healthz",
                Query = "?probe=1"
            },
            Passive = new PassiveHealthCheckConfiguration
            {
                Enabled = true,
                Policy = "TransportFailureRate",
                ReactivationPeriod = TimeSpan.FromMinutes(1)
            }
        };
        _repo.WithApps(app);

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        var hc = config.Clusters[0].HealthCheck;
        hc.ShouldNotBeNull();
        hc!.Active.ShouldNotBeNull();
        hc.Active!.Enabled.ShouldBe(true);
        hc.Active.Interval.ShouldBe(TimeSpan.FromSeconds(30));
        hc.Active.Timeout.ShouldBe(TimeSpan.FromSeconds(5));
        hc.Active.Policy.ShouldBe("ConsecutiveFailures");
        hc.Active.Path.ShouldBe("/healthz");
        hc.Active.Query.ShouldBe("?probe=1");
        hc.Passive.ShouldNotBeNull();
        hc.Passive!.Enabled.ShouldBe(true);
        hc.Passive.Policy.ShouldBe("TransportFailureRate");
        hc.Passive.ReactivationPeriod.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void GetConfig_WithoutHealthCheck_LeavesItNull()
    {
        var app = ApplicationFactory.Create(name: "no-hc", routeHosts: new[] { "x.lan" });
        _repo.WithApps(app);

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        config.Clusters[0].HealthCheck.ShouldBeNull();
    }

    [Theory]
    [InlineData("1.1", "1.1")]
    [InlineData("2", "2.0")]
    [InlineData("2.0", "2.0")]
    [InlineData("3", "3.0")]
    public void GetConfig_HttpRequestVersion_ParsesCommonForms(string input, string expectedToString)
    {
        var app = ApplicationFactory.Create(name: "v", routeHosts: new[] { "x.lan" });
        app.Cluster.HttpRequest = new HttpRequestConfiguration
        {
            ActivityTimeout = TimeSpan.FromSeconds(45),
            Version = input,
            VersionPolicy = "RequestVersionExact",
            AllowResponseBuffering = false
        };
        _repo.WithApps(app);

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        var req = config.Clusters[0].HttpRequest;
        req.ShouldNotBeNull();
        req!.ActivityTimeout.ShouldBe(TimeSpan.FromSeconds(45));
        req.Version.ShouldNotBeNull();
        req.Version!.ToString().ShouldBe(expectedToString);
        req.VersionPolicy.ShouldBe(System.Net.Http.HttpVersionPolicy.RequestVersionExact);
        req.AllowResponseBuffering.ShouldBe(false);
    }

    [Fact]
    public void GetConfig_HttpRequestWithUnknownVersion_LeavesVersionNull()
    {
        var app = ApplicationFactory.Create(name: "vbad", routeHosts: new[] { "x.lan" });
        app.Cluster.HttpRequest = new HttpRequestConfiguration { Version = "garbage" };
        _repo.WithApps(app);

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        config.Clusters[0].HttpRequest!.Version.ShouldBeNull();
    }

    [Fact]
    public void GetConfig_WithEmptyHostsList_SetsHostsToNull()
    {
        var app = ApplicationFactory.Create(name: "no-hosts", routeHosts: Array.Empty<string>());
        _repo.WithApps(app);

        using var provider = new HomeYarpConfigProvider(_repo);
        var config = provider.GetConfig();

        config.Routes.ShouldHaveSingleItem();
        config.Routes[0].Match.Hosts.ShouldBeNull();
    }

    [Fact]
    public void Reload_WhenBuildConfigThrows_DoesNotPropagateAndKeepsPriorConfig()
    {
        // Regression: a throwing reload used to bubble up to the controller via
        // CTS.Cancel(), so the save HTTP request returned 500 and the live YARP
        // config was left half-updated until restart.
        var first = ApplicationFactory.Create(name: "first", routeHosts: new[] { "x.lan" });
        var calls = 0;
        var repo = Substitute.For<IApplicationRepository>();
        repo.GetSnapshot().Returns(_ =>
        {
            calls++;
            if (calls == 1) return ApplicationSnapshot.FromItems(new[] { first });
            throw new InvalidOperationException("simulated reload failure");
        });
        var cts = new CancellationTokenSource();
        repo.GetReloadToken().Returns(_ => new CancellationChangeToken(cts.Token));

        using var provider = new HomeYarpConfigProvider(repo);
        var initial = provider.GetConfig();
        initial.Routes.ShouldHaveSingleItem();

        var firing = cts;
        cts = new CancellationTokenSource();
        Should.NotThrow(() => firing.Cancel());

        // BuildConfig threw inside ReloadConfig — the live snapshot must remain on the prior config.
        provider.GetConfig().ShouldBeSameAs(initial);
    }
}
