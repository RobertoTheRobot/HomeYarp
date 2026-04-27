using HomeYarp.Persistance.Json;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Options;

namespace HomeYarp.Tests.Persistance;

public class JsonApplicationRepositoryTests
{
    private static JsonApplicationRepository NewRepo(string root)
        => new(Options.Create(new JsonStoreOptions { DataRoot = root }));

    [Fact]
    public async Task AddAsync_PersistsAppToDisk_AndCacheReturnsIt()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var app = ApplicationFactory.Create(name: "first");

        await repo.AddAsync(app);

        File.Exists(Path.Combine(dir.Path, "applications", app.Id.ToString("N") + ".json")).ShouldBeTrue();
        var fetched = await repo.GetByIdAsync(app.Id);
        fetched.ShouldNotBeNull();
        fetched!.Name.ShouldBe("first");
    }

    [Fact]
    public async Task GetByNameAsync_IsCaseInsensitive()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        await repo.AddAsync(ApplicationFactory.Create(name: "MixedCase"));

        var found = await repo.GetByNameAsync("mixedcase");

        found.ShouldNotBeNull();
        found!.Name.ShouldBe("MixedCase");
    }

    [Fact]
    public async Task UpdateAsync_OverwritesExistingFile()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var app = ApplicationFactory.Create(name: "x");
        await repo.AddAsync(app);

        app.DisplayName = "Updated";
        await repo.UpdateAsync(app);

        var fetched = await repo.GetByIdAsync(app.Id);
        fetched!.DisplayName.ShouldBe("Updated");
    }

    [Fact]
    public async Task DeleteAsync_RemovesFileAndReturnsTrue()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var app = ApplicationFactory.Create(name: "x");
        await repo.AddAsync(app);
        var path = Path.Combine(dir.Path, "applications", app.Id.ToString("N") + ".json");

        var result = await repo.DeleteAsync(app.Id);

        result.ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
        (await repo.GetByIdAsync(app.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_WhenIdMissing_ReturnsFalse()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);

        var result = await repo.DeleteAsync(Guid.NewGuid());

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task GetReloadToken_FiresOnAdd()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var initial = repo.GetReloadToken();
        initial.HasChanged.ShouldBeFalse();

        await repo.AddAsync(ApplicationFactory.Create(name: "x"));

        initial.HasChanged.ShouldBeTrue();
    }

    [Fact]
    public async Task GetReloadToken_FiresOnUpdate()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var app = ApplicationFactory.Create(name: "x");
        await repo.AddAsync(app);

        var token = repo.GetReloadToken();
        token.HasChanged.ShouldBeFalse();
        await repo.UpdateAsync(app);

        token.HasChanged.ShouldBeTrue();
    }

    [Fact]
    public async Task GetReloadToken_FiresOnSuccessfulDelete()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var app = ApplicationFactory.Create(name: "x");
        await repo.AddAsync(app);

        var token = repo.GetReloadToken();
        await repo.DeleteAsync(app.Id);

        token.HasChanged.ShouldBeTrue();
    }

    [Fact]
    public async Task GetReloadToken_DoesNotFire_WhenDeleteFindsNothing()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var token = repo.GetReloadToken();

        await repo.DeleteAsync(Guid.NewGuid());

        token.HasChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task RoundTrip_PreservesTransformsHealthCheckAndHttpRequest()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var app = ApplicationFactory.Create(name: "advanced", routeHosts: new[] { "x.lan" });
        app.Routes[0].Transforms = new List<RouteTransform>
        {
            new() { ["PathSet"] = "/api/v2" },
            new() { ["RequestHeader"] = "X-User", ["Set"] = "anon" }
        };
        app.Cluster.HealthCheck = new HealthCheckConfiguration
        {
            Active = new ActiveHealthCheckConfiguration
            {
                Enabled = true,
                Interval = TimeSpan.FromSeconds(30),
                Path = "/healthz"
            },
            Passive = new PassiveHealthCheckConfiguration
            {
                Enabled = true,
                Policy = "TransportFailureRate",
                ReactivationPeriod = TimeSpan.FromMinutes(2)
            }
        };
        app.Cluster.HttpRequest = new HttpRequestConfiguration
        {
            ActivityTimeout = TimeSpan.FromSeconds(45),
            Version = "2.0",
            VersionPolicy = "RequestVersionExact",
            AllowResponseBuffering = false
        };

        await repo.AddAsync(app);

        // Simulate restart: spin up a fresh repo against the same directory.
        var fresh = NewRepo(dir.Path);
        var loaded = await fresh.GetByIdAsync(app.Id);

        loaded.ShouldNotBeNull();
        loaded!.Routes[0].Transforms!.Count.ShouldBe(2);
        loaded.Routes[0].Transforms![0]["PathSet"].ShouldBe("/api/v2");
        loaded.Cluster.HealthCheck!.Active!.Path.ShouldBe("/healthz");
        loaded.Cluster.HealthCheck.Passive!.ReactivationPeriod.ShouldBe(TimeSpan.FromMinutes(2));
        loaded.Cluster.HttpRequest!.Version.ShouldBe("2.0");
        loaded.Cluster.HttpRequest.AllowResponseBuffering.ShouldBe(false);
    }

    [Fact]
    public async Task EnsureLoaded_PicksUpFilesWrittenByAnotherInstance()
    {
        using var dir = new TempDirectory();
        var first = NewRepo(dir.Path);
        await first.AddAsync(ApplicationFactory.Create(name: "persisted"));

        // Second instance points at the same directory; should load the existing file on first read.
        var second = NewRepo(dir.Path);
        var fetched = await second.GetByNameAsync("persisted");

        fetched.ShouldNotBeNull();
    }
}
