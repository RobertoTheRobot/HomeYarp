using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Services;
using HomeYarp.Tests.TestHelpers;

namespace HomeYarp.Tests.Application.Services;

public class CertificateServiceCrudTests
{
    private readonly ICertificateRepository _repo = Substitute.For<ICertificateRepository>();

    private CertificateService Service => new(_repo);

    [Fact]
    public async Task ListAsync_DelegatesToRepository()
    {
        var certs = new List<Certificate>
        {
            ApplicationFactory.CreateSelfSignedCert("a", new[] { "x.local" }),
            ApplicationFactory.CreateSelfSignedCert("b", new[] { "y.local" })
        };
        _repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(certs);

        var result = await Service.ListAsync();

        result.ShouldBe(certs);
    }

    [Fact]
    public async Task GetAsync_DelegatesToRepository()
    {
        var cert = ApplicationFactory.CreateSelfSignedCert("z", new[] { "z.local" });
        _repo.GetByIdAsync(cert.Id, Arg.Any<CancellationToken>()).Returns(cert);

        var result = await Service.GetAsync(cert.Id);

        result.ShouldBe(cert);
    }

    [Fact]
    public async Task GetAsync_WhenMissing_ReturnsNull()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);

        var result = await Service.GetAsync(Guid.NewGuid());

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetCertificatePemAsync_WhenMaterialExists_ReturnsPublicPem()
    {
        var id = Guid.NewGuid();
        _repo.GetMaterialAsync(id, Arg.Any<CancellationToken>())
            .Returns(new CertificateMaterial("-----BEGIN CERTIFICATE-----\nABC\n-----END CERTIFICATE-----", "secret-key"));

        var pem = await Service.GetCertificatePemAsync(id);

        pem.ShouldBe("-----BEGIN CERTIFICATE-----\nABC\n-----END CERTIFICATE-----");
    }

    [Fact]
    public async Task GetCertificatePemAsync_WhenMaterialMissing_ReturnsNull()
    {
        _repo.GetMaterialAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((CertificateMaterial?)null);

        var pem = await Service.GetCertificatePemAsync(Guid.NewGuid());

        pem.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_DelegatesToRepository()
    {
        var id = Guid.NewGuid();
        _repo.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await Service.DeleteAsync(id);

        result.ShouldBeTrue();
        await _repo.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }
}
