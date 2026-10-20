using HomeYarp.Application.RootCa;
using HomeYarp.Persistance.Json;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Options;

namespace HomeYarp.Tests.Persistance;

public class FileRootCaStoreTests
{
    private static FileRootCaStore NewStore(string root)
        => new(Options.Create(new JsonStoreOptions { DataRoot = root }));

    [Fact]
    public async Task LoadAsync_WhenNothingSaved_ReturnsNull()
    {
        using var dir = new TempDirectory();

        (await NewStore(dir.Path).LoadAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAcrossInstances()
    {
        using var dir = new TempDirectory();
        var material = new RootCaMaterial("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n",
                                          "-----BEGIN PRIVATE KEY-----\nBBBB\n-----END PRIVATE KEY-----\n");

        await NewStore(dir.Path).SaveAsync(material);
        var loaded = await NewStore(dir.Path).LoadAsync();

        loaded.ShouldBe(material);
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingMaterial()
    {
        using var dir = new TempDirectory();
        var store = NewStore(dir.Path);
        await store.SaveAsync(new RootCaMaterial("cert1", "key1"));
        await store.SaveAsync(new RootCaMaterial("cert2", "key2"));

        (await store.LoadAsync())!.PrivateKeyPem.ShouldBe("key2");
    }

    [Fact]
    public async Task SaveAsync_OnUnix_RestrictsKeyFileAndDirectoryToOwner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var dir = new TempDirectory();
        await NewStore(dir.Path).SaveAsync(new RootCaMaterial("cert", "key"));

        var caDir = Path.Combine(dir.Path, "ca");
        File.GetUnixFileMode(Path.Combine(caDir, "root.key.pem")).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(caDir).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
