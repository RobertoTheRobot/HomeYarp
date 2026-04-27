using HomeYarp.Application.Acme;
using HomeYarp.Persistance.Json;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Options;

namespace HomeYarp.Tests.Persistance;

public class FileAcmeAccountStoreTests
{
    private static FileAcmeAccountStore NewStore(string root)
        => new(Options.Create(new JsonStoreOptions { DataRoot = root }));

    [Fact]
    public async Task LoadAsync_WhenNoFile_ReturnsNull()
    {
        using var dir = new TempDirectory();
        var store = NewStore(dir.Path);

        (await store.LoadAsync("https://example.test/directory")).ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsRecord()
    {
        using var dir = new TempDirectory();
        var store = NewStore(dir.Path);
        var record = new AcmeAccountRecord(
            "https://example.test/directory",
            "ops@example.test",
            "-----BEGIN PRIVATE KEY-----\nMIGHFAKE\n-----END PRIVATE KEY-----\n",
            "https://example.test/account/123",
            DateTimeOffset.UtcNow);

        await store.SaveAsync(record);
        var loaded = await store.LoadAsync(record.DirectoryUrl);

        loaded.ShouldNotBeNull();
        loaded!.DirectoryUrl.ShouldBe(record.DirectoryUrl);
        loaded.Email.ShouldBe(record.Email);
        loaded.KeyPem.ShouldBe(record.KeyPem);
        loaded.RegistrationLocation.ShouldBe(record.RegistrationLocation);
        loaded.AgreedAt.ShouldBe(record.AgreedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task DifferentDirectoryUrls_ProduceIndependentAccounts()
    {
        using var dir = new TempDirectory();
        var store = NewStore(dir.Path);

        var prod = new AcmeAccountRecord(
            "https://acme-v02.api.letsencrypt.org/directory",
            "ops@example.test",
            "-----BEGIN PRIVATE KEY-----\nPROD\n-----END PRIVATE KEY-----\n",
            null,
            DateTimeOffset.UtcNow);
        var staging = new AcmeAccountRecord(
            "https://acme-staging-v02.api.letsencrypt.org/directory",
            "ops@example.test",
            "-----BEGIN PRIVATE KEY-----\nSTAGING\n-----END PRIVATE KEY-----\n",
            null,
            DateTimeOffset.UtcNow);

        await store.SaveAsync(prod);
        await store.SaveAsync(staging);

        (await store.LoadAsync(prod.DirectoryUrl))!.KeyPem.ShouldContain("PROD");
        (await store.LoadAsync(staging.DirectoryUrl))!.KeyPem.ShouldContain("STAGING");

        var files = Directory.GetFiles(Path.Combine(dir.Path, "acme"));
        // Two manifests + two key PEMs = 4 files.
        files.Length.ShouldBe(4);
    }
}
