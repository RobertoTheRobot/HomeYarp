using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Acme;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Application.Services;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Options;

namespace HomeYarp.Tests.Application.Services;

public class ApplicationServiceDeleteTests
{
    private readonly IApplicationRepository _repo = Substitute.For<IApplicationRepository>();
    private readonly ICertificateRepository _certs = Substitute.For<ICertificateRepository>();
    private readonly ISelfSignedCertificateService _selfSigned = Substitute.For<ISelfSignedCertificateService>();
    private readonly IAcmeService _acme = Substitute.For<IAcmeService>();
    private readonly IOptionsMonitor<AcmeOptions> _options = Substitute.For<IOptionsMonitor<AcmeOptions>>();

    public ApplicationServiceDeleteTests()
    {
        _options.CurrentValue.Returns(new AcmeOptions());
    }

    private ApplicationService Service => new(_repo, _certs, _selfSigned, _acme, _options);

    [Fact]
    public async Task DeleteAsync_WhenAppNotFound_ReturnsFalse()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((DomainApplication?)null);

        var result = await Service.DeleteAsync(Guid.NewGuid());

        result.ShouldBeFalse();
        await _certs.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_WhenSourceIsManual_DoesNotDeleteCertificate()
    {
        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Manual,
            certificateId: Guid.NewGuid());

        _repo.GetByIdAsync(app.Id, Arg.Any<CancellationToken>()).Returns(app);
        _repo.DeleteAsync(app.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await Service.DeleteAsync(app.Id);

        result.ShouldBeTrue();
        await _certs.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_WhenSourceInternalWithCertId_DeletesCertificate()
    {
        var certId = Guid.NewGuid();
        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: new[] { "x.example" },
            certificateId: certId);

        _repo.GetByIdAsync(app.Id, Arg.Any<CancellationToken>()).Returns(app);
        _repo.DeleteAsync(app.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await Service.DeleteAsync(app.Id);

        result.ShouldBeTrue();
        await _certs.Received(1).DeleteAsync(certId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_WhenSourceExternalWithCertId_DeletesCertificate()
    {
        var certId = Guid.NewGuid();
        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.External,
            routeHosts: new[] { "x.example" },
            certificateId: certId);

        _repo.GetByIdAsync(app.Id, Arg.Any<CancellationToken>()).Returns(app);
        _repo.DeleteAsync(app.Id, Arg.Any<CancellationToken>()).Returns(true);

        await Service.DeleteAsync(app.Id);

        await _certs.Received(1).DeleteAsync(certId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_WhenRepoDeleteReturnsFalse_DoesNotDeleteCert()
    {
        var certId = Guid.NewGuid();
        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: new[] { "x.example" },
            certificateId: certId);

        _repo.GetByIdAsync(app.Id, Arg.Any<CancellationToken>()).Returns(app);
        _repo.DeleteAsync(app.Id, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Service.DeleteAsync(app.Id);

        result.ShouldBeFalse();
        await _certs.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
