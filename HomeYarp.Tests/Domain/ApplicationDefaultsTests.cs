namespace HomeYarp.Tests.Domain;

public class ApplicationDefaultsTests
{
    [Fact]
    public void NewApplication_HasGuidId_AndIsEnabled()
    {
        var app = new DomainApplication { Name = "smoke" };

        app.Id.ShouldNotBe(Guid.Empty);
        app.Enabled.ShouldBeTrue();
        app.Routes.ShouldBeEmpty();
        app.Cluster.ShouldNotBeNull();
        app.Tls.ShouldNotBeNull();
        app.Tls.Mode.ShouldBe(TlsMode.None);
        app.Tls.Source.ShouldBe(TlsCertificateSource.Manual);
    }

    [Fact]
    public void Timestamps_DefaultToUtcNow()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var app = new DomainApplication { Name = "smoke" };

        var after = DateTimeOffset.UtcNow.AddSeconds(1);
        app.CreatedAt.ShouldBeInRange(before, after);
        app.UpdatedAt.ShouldBeInRange(before, after);
    }
}
