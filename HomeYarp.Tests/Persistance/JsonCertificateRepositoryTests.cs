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
    public async Task GetReloadToken_FiresOnSave()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var token = repo.GetReloadToken();

        var cert = ApplicationFactory.CreateSelfSignedCert("x", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");
        await repo.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));

        token.HasChanged.ShouldBeTrue();
    }

    [Fact]
    public async Task GetReloadToken_FiresOnDelete()
    {
        using var dir = new TempDirectory();
        var repo = NewRepo(dir.Path);
        var cert = ApplicationFactory.CreateSelfSignedCert("x", new[] { "x.local" });
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("x.local");
        await repo.SaveAsync(cert, new CertificateMaterial(certPem, keyPem));
        var token = repo.GetReloadToken();

        await repo.DeleteAsync(cert.Id);

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
}
