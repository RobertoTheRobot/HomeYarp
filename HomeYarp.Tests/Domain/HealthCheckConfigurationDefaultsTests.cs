namespace HomeYarp.Tests.Domain;

public class HealthCheckConfigurationDefaultsTests
{
    [Fact]
    public void NewHealthCheckConfiguration_AllFieldsNull()
    {
        var hc = new HealthCheckConfiguration();

        hc.Active.ShouldBeNull();
        hc.Passive.ShouldBeNull();
    }

    [Fact]
    public void NewActiveHealthCheckConfiguration_AllFieldsNull()
    {
        var active = new ActiveHealthCheckConfiguration();

        active.Enabled.ShouldBeNull();
        active.Interval.ShouldBeNull();
        active.Timeout.ShouldBeNull();
        active.Policy.ShouldBeNull();
        active.Path.ShouldBeNull();
        active.Query.ShouldBeNull();
    }

    [Fact]
    public void NewPassiveHealthCheckConfiguration_AllFieldsNull()
    {
        var passive = new PassiveHealthCheckConfiguration();

        passive.Enabled.ShouldBeNull();
        passive.Policy.ShouldBeNull();
        passive.ReactivationPeriod.ShouldBeNull();
    }

    [Fact]
    public void NewHttpRequestConfiguration_AllFieldsNull()
    {
        var req = new HttpRequestConfiguration();

        req.ActivityTimeout.ShouldBeNull();
        req.Version.ShouldBeNull();
        req.VersionPolicy.ShouldBeNull();
        req.AllowResponseBuffering.ShouldBeNull();
    }

    [Fact]
    public void RouteDefinition_TransformsDefaultsToNull()
    {
        var route = new RouteDefinition();

        route.Transforms.ShouldBeNull();
    }

    [Fact]
    public void ClusterDefinition_HealthCheckAndHttpRequestDefaultToNull()
    {
        var cluster = new ClusterDefinition();

        cluster.HealthCheck.ShouldBeNull();
        cluster.HttpRequest.ShouldBeNull();
    }
}
