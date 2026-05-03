using HomeYarp.Application.Abstractions;
using HomeYarp.Persistance.Json;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Options;

namespace HomeYarp.Tests.Persistance;

public class JsonCertificateRepositoryTests
{
    private static JsonCertificateRepository NewRepo(string root)
        => new(Options.Create(new JsonStoreOptions { DataRoot = root }));

    [Fact]
    public async Task SaveAsync_WritesManifestAndPemFiles()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var cert = ApplicationFactory.CreateSelfSignedCert("mycert", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");

        await repo.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));

        var folder = Path.Combine(dir.Path, "certificates");
        File.Exists(Path.Combine(folder, cert.Id.ToString("N") + ".json")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, cert.Id.ToString("N") + ".cert.pem")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, cert.Id.ToString("N") + ".key.pem")).ShouldBeTrue();
    }

    [Fact]
    public async Task GetMaterialAsync_RoundTripsCertAndKey()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var cert = ApplicationFactory.CreateSelfSignedCert("mycert", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");
        await repo.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));

        var material = await repo.GetMaterialAsync(cert.Id);

        material.ShouldNotBeNull();
        material!.CertificatePem.Trim().ShouldBe(certPem.Trim());
        material.PrivateKeyPem.Trim().ShouldBe(keyPem.Trim());
    }

    [Fact]
    public async Task GetMaterialAsync_WhenMissing_ReturnsNull()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);

        (await repo.GetMaterialAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesAllThreeFiles()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var cert = ApplicationFactory.CreateSelfSignedCert("mycert", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");
        await repo.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));

        var result = await repo.DeleteAsync(cert.Id);

        result.ShouldBeTrue();
        var folder = Path.Combine(dir.Path, "certificates");
        File.Exists(Path.Combine(folder, cert.Id.ToString("N") + ".json")).ShouldBeFalse();
        File.Exists(Path.Combine(folder, cert.Id.ToString("N") + ".cert.pem")).ShouldBeFalse();
        File.Exists(Path.Combine(folder, cert.Id.ToString("N") + ".key.pem")).ShouldBeFalse();
    }

    [Fact]
    public async Task GetReloadToken_DoesNotFireOnSave_BecauseReloadIsNowManual()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var token = repo.GetReloadToken();

        var cert = ApplicationFactory.CreateSelfSignedCert("x", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");
        await repo.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));

        // Save no longer auto-fires reload — IRuntimeReloadService does it on demand.
        token.HasChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task GetReloadToken_DoesNotFireOnDelete_BecauseReloadIsNowManual()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var cert = ApplicationFactory.CreateSelfSignedCert("x", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");
        await repo.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));
        var token = repo.GetReloadToken();

        await repo.DeleteAsync(cert.Id);

        token.HasChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task SignalReload_FiresChangeToken()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var cert = ApplicationFactory.CreateSelfSignedCert("x", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");
        await repo.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));

        var token = repo.GetReloadToken();
        token.HasChanged.ShouldBeFalse();
        repo.SignalReload();

        token.HasChanged.ShouldBeTrue();
    }

    [Fact]
    public async Task GetByNameAsync_IsCaseInsensitive()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var cert = ApplicationFactory.CreateSelfSignedCert("MixedCase", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");
        await repo.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));

        var found = await repo.GetByNameAsync("mixedcase");

        found.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetSnapshot_AfterSave_ContainsNewItem()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var cert = ApplicationFactory.CreateSelfSignedCert("snap", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");

        await repo.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));
        var snapshot = repo.GetSnapshot();

        snapshot.All.Length.ShouldBe(1);
        snapshot.ById[cert.Id].Name.ShouldBe("snap");
        snapshot.ByName["SNAP"].Id.ShouldBe(cert.Id);
    }

    [Fact]
    public async Task GetSnapshot_AfterDelete_OmitsItem()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var cert = ApplicationFactory.CreateSelfSignedCert("x", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");
        await repo.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));

        await repo.DeleteAsync(cert.Id);
        var snapshot = repo.GetSnapshot();

        snapshot.All.Length.ShouldBe(0);
        snapshot.ById.ContainsKey(cert.Id).ShouldBeFalse();
        snapshot.ByName.ContainsKey("x").ShouldBeFalse();
    }

    [Fact]
    public async Task GetSnapshot_ReturnsSameInstanceUntilNextWrite()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var cert = ApplicationFactory.CreateSelfSignedCert("x", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");
        await repo.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));

        var first = repo.GetSnapshot();
        var second = repo.GetSnapshot();
        ReferenceEquals(first, second).ShouldBeTrue();

        var another = ApplicationFactory.CreateSelfSignedCert("y", new[] { "y.local" });
        var (cp2, kp2) = CertificateFactory.GenerateSelfSignedPem("y.local");
        await repo.SaveAsync(another, new CertificateMaterial(cp2, kp2));
        var third = repo.GetSnapshot();
        ReferenceEquals(first, third).ShouldBeFalse();
    }

    [Fact]
    public async Task Constructor_LoadsExistingManifestsIntoSnapshot()
    {
        using var dir = new TempDirectory();
        var first = NewRepo(dir.Path);
        var cert = ApplicationFactory.CreateSelfSignedCert("preloaded", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");
        await first.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));

        var second = NewRepo(dir.Path);
        var snapshot = second.GetSnapshot();

        snapshot.All.Length.ShouldBe(1);
        snapshot.ByName["preloaded"].ShouldNotBeNull();
    }
}
