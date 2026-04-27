using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Services;
using HomeYarp.Tests.TestHelpers;

namespace HomeYarp.Tests.Application.Services;

public class CertificateServiceUploadTests
{
    private readonly ICertificateRepository _repo = Substitute.For<ICertificateRepository>();

    private CertificateService Service => new(_repo);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task UploadAsync_WhenNameBlank_ThrowsArgumentException(string? name)
    {
        var (cert, key) = CertificateFactory.GenerateSelfSignedPem("test.local");

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            Service.UploadAsync(name!, null, new CertificateMaterial(cert, key)));
        ex.Message.ShouldContain("name is required", Case.Insensitive);
    }

    [Fact]
    public async Task UploadAsync_WhenNameAlreadyExists_ThrowsInvalidOperationException()
    {
        var existing = ApplicationFactory.CreateSelfSignedCert("dup", new[] { "x.local" });
        _repo.GetByNameAsync("dup", Arg.Any<CancellationToken>()).Returns(existing);

        var (cert, key) = CertificateFactory.GenerateSelfSignedPem("test.local");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Service.UploadAsync("dup", null, new CertificateMaterial(cert, key)));
        ex.Message.ShouldContain("already exists", Case.Insensitive);
    }

    [Fact]
    public async Task UploadAsync_WhenPemInvalid_ThrowsArgumentException()
    {
        _repo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            Service.UploadAsync("x", null, new CertificateMaterial("not a pem", "still not a pem")));
        ex.Message.ShouldContain("Invalid PEM", Case.Insensitive);
    }

    [Fact]
    public async Task UploadAsync_OnSuccess_PopulatesMetadataAndPersists()
    {
        _repo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);

        var hostnames = new[] { "test.example.com", "alt.example.com" };
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem(hostnames);

        var result = await Service.UploadAsync("uploaded", "Friendly", new CertificateMaterial(certPem, keyPem));

        result.Name.ShouldBe("uploaded");
        result.FriendlyName.ShouldBe("Friendly");
        result.Subject.ShouldContain("test.example.com");
        result.Issuer.ShouldNotBeNullOrWhiteSpace();
        result.Thumbprint.ShouldNotBeNullOrWhiteSpace();
        result.SubjectAlternativeNames.ShouldContain("test.example.com");
        result.SubjectAlternativeNames.ShouldContain("alt.example.com");
        result.NotAfter.ShouldBeGreaterThan(result.NotBefore);
        result.Acme.ShouldBeNull();
        result.SelfSigned.ShouldBeNull();

        await _repo.Received(1).SaveAsync(
            Arg.Any<Certificate>(),
            Arg.Is<CertificateMaterial>(m => m.CertificatePem == certPem && m.PrivateKeyPem == keyPem),
            Arg.Any<CancellationToken>());
    }
}
