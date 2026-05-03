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
    public async Task GetReloadToken_DoesNotFireOnAdd_BecauseReloadIsNowManual()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var initial = repo.GetReloadToken();
        initial.HasChanged.ShouldBeFalse();

        await repo.AddAsync(ApplicationFactory.Create(name: "x"));

        // Add no longer auto-fires reload — IRuntimeReloadService does it on demand.
        initial.HasChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task GetReloadToken_DoesNotFireOnUpdate_BecauseReloadIsNowManual()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var app = ApplicationFactory.Create(name: "x");
        await repo.AddAsync(app);

        var token = repo.GetReloadToken();
        token.HasChanged.ShouldBeFalse();
        await repo.UpdateAsync(app);

        token.HasChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task GetReloadToken_DoesNotFireOnDelete_BecauseReloadIsNowManual()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var app = ApplicationFactory.Create(name: "x");
        await repo.AddAsync(app);

        var token = repo.GetReloadToken();
        await repo.DeleteAsync(app.Id);

        token.HasChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task SignalReload_FiresChangeToken()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        await repo.AddAsync(ApplicationFactory.Create(name: "x"));

        var token = repo.GetReloadToken();
        token.HasChanged.ShouldBeFalse();
        repo.SignalReload();

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

    [Fact]
    public async Task GetSnapshot_AfterAdd_ContainsNewItem()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var app = ApplicationFactory.Create(name: "snap");

        await repo.AddAsync(app);
        var snapshot = repo.GetSnapshot();

        snapshot.All.Length.ShouldBe(1);
        snapshot.ById[app.Id].Name.ShouldBe("snap");
        snapshot.ByName["snap"].Id.ShouldBe(app.Id);
        snapshot.ByName["SNAP"].Id.ShouldBe(app.Id); // case-insensitive
    }

    [Fact]
    public async Task GetSnapshot_AfterUpdate_ReflectsNewState()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var app = ApplicationFactory.Create(name: "x");
        await repo.AddAsync(app);

        app.DisplayName = "Renamed";
        await repo.UpdateAsync(app);
        var snapshot = repo.GetSnapshot();

        snapshot.ById[app.Id].DisplayName.ShouldBe("Renamed");
    }

    [Fact]
    public async Task GetSnapshot_AfterRename_DropsOldNameBinding()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var app = ApplicationFactory.Create(name: "old");
        await repo.AddAsync(app);

        app.Name = "new";
        await repo.UpdateAsync(app);
        var snapshot = repo.GetSnapshot();

        snapshot.ByName.ContainsKey("old").ShouldBeFalse();
        snapshot.ByName["new"].Id.ShouldBe(app.Id);
    }

    [Fact]
    public async Task GetSnapshot_AfterDelete_OmitsItem()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var app = ApplicationFactory.Create(name: "x");
        await repo.AddAsync(app);

        await repo.DeleteAsync(app.Id);
        var snapshot = repo.GetSnapshot();

        snapshot.All.Length.ShouldBe(0);
        snapshot.ById.ContainsKey(app.Id).ShouldBeFalse();
        snapshot.ByName.ContainsKey("x").ShouldBeFalse();
    }

    [Fact]
    public async Task GetSnapshot_ReturnsSameInstanceUntilNextWrite()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        await repo.AddAsync(ApplicationFactory.Create(name: "x"));

        var first = repo.GetSnapshot();
        var second = repo.GetSnapshot();

        // Identity equality — readers see a stable atomic reference between writes.
        ReferenceEquals(first, second).ShouldBeTrue();

        await repo.AddAsync(ApplicationFactory.Create(name: "y"));
        var third = repo.GetSnapshot();
        ReferenceEquals(first, third).ShouldBeFalse();
    }

    [Fact]
    public async Task GetSnapshot_ConcurrentReadsAndWrites_ReadersAlwaysSeeConsistentSnapshot()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        // Seed so readers always have something to inspect.
        await repo.AddAsync(ApplicationFactory.Create(name: "seed"));

        var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var writer = Task.Run(async () =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                var app = ApplicationFactory.Create(name: $"app-{i++}");
                await repo.AddAsync(app);
            }
        });

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = repo.GetSnapshot();
                // Internal consistency: All length matches ById count, every All item is in ById.
                snapshot.ById.Count.ShouldBe(snapshot.All.Length);
                foreach (var item in snapshot.All)
                {
                    snapshot.ById.ContainsKey(item.Id).ShouldBeTrue();
                }
            }
        });

        await Task.WhenAll(writer, reader);
    }

    [Fact]
    public async Task Constructor_LoadsExistingFilesIntoSnapshot()
    {
        using var dir = new TempDirectory();
        // Seed via a first repo, then verify a fresh repo's GetSnapshot is populated synchronously
        // (not lazy on first read).
        var first = NewRepo(dir.Path);
        await first.AddAsync(ApplicationFactory.Create(name: "preloaded"));

        var second = NewRepo(dir.Path);
        var snapshot = second.GetSnapshot();

        snapshot.All.Length.ShouldBe(1);
        snapshot.ByName["preloaded"].ShouldNotBeNull();
    }
}
