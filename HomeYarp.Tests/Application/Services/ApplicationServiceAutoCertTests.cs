using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Acme;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Application.Services;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Options;

namespace HomeYarp.Tests.Application.Services;

public class ApplicationServiceAutoCertTests
{
    private readonly IApplicationRepository _repo = Substitute.For<IApplicationRepository>();
    private readonly ICertificateRepository _certs = Substitute.For<ICertificateRepository>();
    private readonly ISelfSignedCertificateService _selfSigned = Substitute.For<ISelfSignedCertificateService>();
    private readonly IAcmeService _acme = Substitute.For<IAcmeService>();
    private readonly IOptionsMonitor<AcmeOptions> _options = Substitute.For<IOptionsMonitor<AcmeOptions>>();

    public ApplicationServiceAutoCertTests()
    {
        _options.CurrentValue.Returns(new AcmeOptions
        {
            Enabled = true,
            AgreeToTermsOfService = true,
            AccountEmail = "ops@example.test",
            DirectoryUrl = "https://example.test/directory"
        });
        _repo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((DomainApplication?)null);
    }

    private ApplicationService Service => new(_repo, _certs, _selfSigned, _acme, _options);

    [Fact]
    public async Task CreateAsync_FirstTimeInternal_IssuesSelfSignedAndSetsCertificateId()
    {
        var newCert = ApplicationFactory.CreateSelfSignedCert("test-app-internal", new[] { "ha.home.lan" });
        _selfSigned.IssueAsync(
            "test-app-internal",
            Arg.Any<string?>(),
            Arg.Is<IReadOnlyList<string>>(h => h.Single() == "ha.home.lan"),
            CertificateKeyType.Ec256,
            365,
            Arg.Any<CancellationToken>())
            .Returns(newCert);

        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: new[] { "ha.home.lan" });

        var result = await Service.CreateAsync(app);

        result.Tls.CertificateId.ShouldBe(newCert.Id);
        await _selfSigned.Received(1).IssueAsync(
            "test-app-internal",
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>>(),
            CertificateKeyType.Ec256,
            365,
            Arg.Any<CancellationToken>());
        await _acme.DidNotReceive().IssueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_FirstTimeExternal_IssuesViaAcme()
    {
        var newCert = ApplicationFactory.CreateAcmeCert("test-app-external", new[] { "cloud.example.com" });
        _acme.IssueAsync(
            "test-app-external",
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>())
            .Returns(newCert);

        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.External,
            routeHosts: new[] { "cloud.example.com" });

        var result = await Service.CreateAsync(app);

        result.Tls.CertificateId.ShouldBe(newCert.Id);
        await _acme.Received(1).IssueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_FirstTimeExternal_WhenAcmeNotConfigured_ThrowsInvalidOperation()
    {
        _options.CurrentValue.Returns(new AcmeOptions { Enabled = false });

        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.External,
            routeHosts: new[] { "cloud.example.com" });

        await Should.ThrowAsync<InvalidOperationException>(() => Service.CreateAsync(app));
        await _acme.DidNotReceive().IssueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_SourceSwitchInternalToManual_DeletesOldAutoCert()
    {
        var oldCertId = Guid.NewGuid();
        var existing = ApplicationFactory.Create(
            name: "x",
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: new[] { "x.example" },
            certificateId: oldCertId);

        _repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        var newCertId = Guid.NewGuid();
        var incoming = ApplicationFactory.Create(
            name: "x",
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Manual,
            certificateId: newCertId);

        await Service.UpdateAsync(existing.Id, incoming);

        await _certs.Received(1).DeleteAsync(oldCertId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_SourceSwitchInternalToExternal_DeletesOldCertAndIssuesNew()
    {
        var oldCertId = Guid.NewGuid();
        var existing = ApplicationFactory.Create(
            name: "x",
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: new[] { "x.example.com" },
            certificateId: oldCertId);

        _repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        var newCert = ApplicationFactory.CreateAcmeCert("x-external", new[] { "x.example.com" });
        _acme.IssueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
             .Returns(newCert);

        var incoming = ApplicationFactory.Create(
            name: "x",
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.External,
            routeHosts: new[] { "x.example.com" });

        var result = await Service.UpdateAsync(existing.Id, incoming);

        await _certs.Received(1).DeleteAsync(oldCertId, Arg.Any<CancellationToken>());
        result.Tls.CertificateId.ShouldBe(newCert.Id);
    }

    [Fact]
    public async Task UpdateAsync_SourceUnchangedInternalAndHostnamesUnchanged_ReusesExistingCert()
    {
        var certId = Guid.NewGuid();
        var existingCert = ApplicationFactory.CreateSelfSignedCert("x-internal", new[] { "x.example" });
        existingCert = new Certificate
        {
            Id = certId,
            Name = existingCert.Name,
            Subject = existingCert.Subject,
            Issuer = existingCert.Issuer,
            Thumbprint = existingCert.Thumbprint,
            NotBefore = existingCert.NotBefore,
            NotAfter = existingCert.NotAfter,
            SubjectAlternativeNames = existingCert.SubjectAlternativeNames,
            SelfSigned = existingCert.SelfSigned
        };

        var existing = ApplicationFactory.Create(
            name: "x",
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: new[] { "x.example" },
            certificateId: certId);

        _repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _certs.GetByIdAsync(certId, Arg.Any<CancellationToken>()).Returns(existingCert);

        var incoming = ApplicationFactory.Create(
            name: "x",
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: new[] { "x.example" });

        var result = await Service.UpdateAsync(existing.Id, incoming);

        result.Tls.CertificateId.ShouldBe(certId);
        await _selfSigned.DidNotReceive().IssueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CertificateKeyType>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _selfSigned.DidNotReceive().RegenerateAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _certs.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_SourceInternalAndHostnamesChanged_RegeneratesCert()
    {
        var certId = Guid.NewGuid();
        var existingCert = new Certificate
        {
            Id = certId,
            Name = "x-internal",
            Subject = "CN=old",
            Issuer = "CN=old",
            Thumbprint = "AAAA",
            NotBefore = DateTimeOffset.UtcNow,
            NotAfter = DateTimeOffset.UtcNow.AddDays(365),
            SelfSigned = new SelfSignedMetadata
            {
                Hostnames = new List<string> { "old.example" },
                KeyType = CertificateKeyType.Ec256,
                ValidityDays = 365
            }
        };

        var existing = ApplicationFactory.Create(
            name: "x",
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: new[] { "old.example" },
            certificateId: certId);

        _repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _certs.GetByIdAsync(certId, Arg.Any<CancellationToken>()).Returns(existingCert);

        var regenerated = new Certificate
        {
            Id = certId,
            Name = "x-internal",
            SelfSigned = new SelfSignedMetadata { Hostnames = new List<string> { "new.example" } }
        };
        _selfSigned.RegenerateAsync(certId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                   .Returns(regenerated);

        var incoming = ApplicationFactory.Create(
            name: "x",
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: new[] { "new.example" });

        var result = await Service.UpdateAsync(existing.Id, incoming);

        result.Tls.CertificateId.ShouldBe(certId);
        await _selfSigned.Received(1).RegenerateAsync(certId, Arg.Is<IReadOnlyList<string>>(h => h.Single() == "new.example"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_SourceExternalAndHostnamesChanged_ThrowsInvalidOperation()
    {
        var certId = Guid.NewGuid();
        var existingCert = new Certificate
        {
            Id = certId,
            Name = "x-external",
            Acme = new AcmeMetadata
            {
                Hostnames = new List<string> { "old.example.com" },
                AccountEmail = "a@b.c",
                DirectoryUrl = "https://example.test/directory"
            }
        };

        var existing = ApplicationFactory.Create(
            name: "x",
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.External,
            routeHosts: new[] { "old.example.com" },
            certificateId: certId);

        _repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _certs.GetByIdAsync(certId, Arg.Any<CancellationToken>()).Returns(existingCert);

        var incoming = ApplicationFactory.Create(
            name: "x",
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.External,
            routeHosts: new[] { "new.example.com" });

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Service.UpdateAsync(existing.Id, incoming));
        ex.Message.ShouldContain("not supported", Case.Insensitive);
    }

    [Fact]
    public async Task UpdateAsync_HostnameComparison_IsCaseInsensitiveAndOrderAgnostic()
    {
        var certId = Guid.NewGuid();
        var existingCert = new Certificate
        {
            Id = certId,
            Name = "x-internal",
            SelfSigned = new SelfSignedMetadata
            {
                Hostnames = new List<string> { "ALPHA.example", "beta.example" }
            }
        };

        var existing = ApplicationFactory.Create(
            name: "x",
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: new[] { "ALPHA.example", "beta.example" },
            certificateId: certId);

        _repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _certs.GetByIdAsync(certId, Arg.Any<CancellationToken>()).Returns(existingCert);

        var incoming = ApplicationFactory.Create(
            name: "x",
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: new[] { "beta.example", "alpha.example" });

        await Service.UpdateAsync(existing.Id, incoming);

        await _selfSigned.DidNotReceive().RegenerateAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
