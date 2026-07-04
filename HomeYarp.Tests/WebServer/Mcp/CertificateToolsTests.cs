using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Acme;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Application.Services;
using HomeYarp.Tests.TestHelpers;
using HomeYarp.WebServer.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace HomeYarp.Tests.WebServer.Mcp;

public class CertificateToolsTests
{
    private readonly ICertificateService _service = Substitute.For<ICertificateService>();
    private readonly IAcmeService _acme = Substitute.For<IAcmeService>();
    private readonly ISelfSignedCertificateService _selfSigned = Substitute.For<ISelfSignedCertificateService>();

    private CertificateTools NewTools() =>
        new(_service, _acme, _selfSigned, NullLogger<CertificateTools>.Instance);

    // list_certificates

    [Fact]
    public async Task ListCertificates_ReturnsMappedResponses()
    {
        _service.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            ApplicationFactory.CreateSelfSignedCert("a", new[] { "x.local" }),
            ApplicationFactory.CreateSelfSignedCert("b", new[] { "y.local" })
        });

        var result = await NewTools().ListCertificates();

        result.Count.ShouldBe(2);
        result.ShouldContain(r => r.Name == "a");
        result.ShouldContain(r => r.Name == "b");
    }

    [Fact]
    public async Task ListCertificates_WhenEmpty_ReturnsEmptyList()
    {
        _service.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Certificate>());

        var result = await NewTools().ListCertificates();

        result.ShouldBeEmpty();
    }

    // get_certificate

    [Fact]
    public async Task GetCertificate_WhenFound_ReturnsMappedResponse()
    {
        var cert = ApplicationFactory.CreateSelfSignedCert("found", new[] { "found.local" });
        _service.GetAsync(cert.Id, Arg.Any<CancellationToken>()).Returns(cert);

        var result = await NewTools().GetCertificate(cert.Id);

        result.Name.ShouldBe("found");
    }

    [Fact]
    public async Task GetCertificate_WhenMissing_ThrowsMcpException()
    {
        _service.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);

        var id = Guid.NewGuid();
        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().GetCertificate(id));

        ex.Message.ShouldContain(id.ToString());
        ex.Message.ShouldContain("was not found");
    }

    // download_certificate_pem

    [Fact]
    public async Task DownloadCertificatePem_WhenFoundWithPem_ReturnsPemString()
    {
        var cert = ApplicationFactory.CreateSelfSignedCert("pem-cert", new[] { "x.local" });
        _service.GetAsync(cert.Id, Arg.Any<CancellationToken>()).Returns(cert);
        _service.GetCertificatePemAsync(cert.Id, Arg.Any<CancellationToken>()).Returns("-----BEGIN CERTIFICATE-----\nfake\n-----END CERTIFICATE-----\n");

        var result = await NewTools().DownloadCertificatePem(cert.Id);

        result.ShouldContain("BEGIN CERTIFICATE");
    }

    [Fact]
    public async Task DownloadCertificatePem_WhenCertMissing_ThrowsMcpException()
    {
        _service.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);

        var id = Guid.NewGuid();
        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().DownloadCertificatePem(id));

        ex.Message.ShouldContain(id.ToString());
        ex.Message.ShouldContain("was not found");
    }

    [Fact]
    public async Task DownloadCertificatePem_WhenPemMissing_ThrowsMcpExceptionAboutMaterial()
    {
        var cert = ApplicationFactory.CreateSelfSignedCert("no-pem", new[] { "x.local" });
        _service.GetAsync(cert.Id, Arg.Any<CancellationToken>()).Returns(cert);
        _service.GetCertificatePemAsync(cert.Id, Arg.Any<CancellationToken>()).Returns((string?)null);

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().DownloadCertificatePem(cert.Id));

        ex.Message.ShouldContain("PEM material is missing");
    }

    // upload_certificate

    [Fact]
    public async Task UploadCertificate_OnSuccess_ReturnsMappedResponse()
    {
        var cert = ApplicationFactory.CreateSelfSignedCert("uploaded", new[] { "x.local" });
        _service.UploadAsync("uploaded", null, Arg.Any<CertificateMaterial>(), Arg.Any<CancellationToken>())
                .Returns(cert);

        var result = await NewTools().UploadCertificate("uploaded", null, "cert-pem", "key-pem");

        result.Name.ShouldBe("uploaded");
    }

    [Fact]
    public async Task UploadCertificate_WhenArgumentException_ThrowsMcpExceptionWithValidationPrefix()
    {
        _service.UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CertificateMaterial>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new ArgumentException("invalid PEM"));

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().UploadCertificate("x", null, "bad", "bad"));

        ex.Message.ShouldStartWith("Validation failed:");
        ex.Message.ShouldContain("invalid PEM");
    }

    [Fact]
    public async Task UploadCertificate_WhenInvalidOperationException_ThrowsMcpExceptionWithOriginalMessage()
    {
        _service.UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CertificateMaterial>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("name already in use"));

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().UploadCertificate("x", null, "cert", "key"));

        ex.Message.ShouldBe("name already in use");
    }

    // issue_self_signed_certificate

    [Fact]
    public async Task IssueSelfSignedCertificate_OnSuccess_ReturnsMappedResponse()
    {
        var cert = ApplicationFactory.CreateSelfSignedCert("self", new[] { "x.local" });
        _selfSigned.IssueAsync("self", null, Arg.Any<IReadOnlyList<string>>(), CertificateKeyType.Ec256, 365, Arg.Any<CancellationToken>())
                   .Returns(cert);

        var result = await NewTools().IssueSelfSignedCertificate("self", null, new[] { "x.local" }, null, null);

        result.Name.ShouldBe("self");
    }

    [Fact]
    public async Task IssueSelfSignedCertificate_WhenArgumentException_ThrowsMcpExceptionWithValidationPrefix()
    {
        _selfSigned.IssueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CertificateKeyType>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .ThrowsAsync(new ArgumentException("hostnames required"));

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().IssueSelfSignedCertificate("x", null, new[] { "x.local" }, null, null));

        ex.Message.ShouldStartWith("Validation failed:");
    }

    [Fact]
    public async Task IssueSelfSignedCertificate_WhenInvalidOperationException_ThrowsMcpExceptionWithOriginalMessage()
    {
        _selfSigned.IssueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CertificateKeyType>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .ThrowsAsync(new InvalidOperationException("duplicate name"));

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().IssueSelfSignedCertificate("x", null, new[] { "x.local" }, null, null));

        ex.Message.ShouldBe("duplicate name");
    }

    // regenerate_certificate

    [Fact]
    public async Task RegenerateCertificate_OnSuccess_ReturnsMappedResponse()
    {
        var cert = ApplicationFactory.CreateSelfSignedCert("regen", new[] { "x.local" });
        _selfSigned.RegenerateAsync(cert.Id, Arg.Any<CancellationToken>()).Returns(cert);

        var result = await NewTools().RegenerateCertificate(cert.Id);

        result.Name.ShouldBe("regen");
    }

    [Fact]
    public async Task RegenerateCertificate_WhenArgumentException_ThrowsMcpExceptionNotFound()
    {
        _selfSigned.RegenerateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                   .ThrowsAsync(new ArgumentException("no such cert"));

        var id = Guid.NewGuid();
        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().RegenerateCertificate(id));

        ex.Message.ShouldContain(id.ToString());
        ex.Message.ShouldContain("was not found");
    }

    [Fact]
    public async Task RegenerateCertificate_WhenInvalidOperationException_ThrowsMcpExceptionWithOriginalMessage()
    {
        _selfSigned.RegenerateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                   .ThrowsAsync(new InvalidOperationException("not a self-signed cert"));

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().RegenerateCertificate(Guid.NewGuid()));

        ex.Message.ShouldBe("not a self-signed cert");
    }

    // issue_acme_certificate

    [Fact]
    public async Task IssueAcmeCertificate_OnSuccess_ReturnsMappedResponse()
    {
        var cert = ApplicationFactory.CreateAcmeCert("acme-cert", new[] { "x.example.com" });
        _acme.IssueAsync("acme-cert", null, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
             .Returns(cert);

        var result = await NewTools().IssueAcmeCertificate("acme-cert", null, new[] { "x.example.com" });

        result.Name.ShouldBe("acme-cert");
    }

    [Fact]
    public async Task IssueAcmeCertificate_WhenArgumentException_ThrowsMcpExceptionWithValidationPrefix()
    {
        _acme.IssueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new ArgumentException("wildcards not supported"));

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().IssueAcmeCertificate("x", null, new[] { "*.example.com" }));

        ex.Message.ShouldStartWith("Validation failed:");
    }

    [Fact]
    public async Task IssueAcmeCertificate_WhenInvalidOperationException_ThrowsMcpExceptionWithOriginalMessage()
    {
        _acme.IssueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new InvalidOperationException("ACME not configured"));

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().IssueAcmeCertificate("x", null, new[] { "x.example.com" }));

        ex.Message.ShouldBe("ACME not configured");
    }

    // renew_certificate

    [Fact]
    public async Task RenewCertificate_OnSuccess_ReturnsMappedResponse()
    {
        var cert = ApplicationFactory.CreateAcmeCert("renewed", new[] { "x.example.com" });
        _acme.RenewAsync(cert.Id, Arg.Any<CancellationToken>()).Returns(cert);

        var result = await NewTools().RenewCertificate(cert.Id);

        result.Name.ShouldBe("renewed");
    }

    [Fact]
    public async Task RenewCertificate_WhenArgumentException_ThrowsMcpExceptionNotFound()
    {
        _acme.RenewAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new ArgumentException("no such cert"));

        var id = Guid.NewGuid();
        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().RenewCertificate(id));

        ex.Message.ShouldContain(id.ToString());
        ex.Message.ShouldContain("was not found");
    }

    [Fact]
    public async Task RenewCertificate_WhenInvalidOperationException_ThrowsMcpExceptionWithOriginalMessage()
    {
        _acme.RenewAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new InvalidOperationException("not an ACME cert"));

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().RenewCertificate(Guid.NewGuid()));

        ex.Message.ShouldBe("not an ACME cert");
    }

    // delete_certificate

    [Fact]
    public async Task DeleteCertificate_WhenRemoved_ReturnsConfirmation()
    {
        var id = Guid.NewGuid();
        _service.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await NewTools().DeleteCertificate(id);

        result.ShouldContain(id.ToString());
        result.ShouldContain("deleted");
    }

    [Fact]
    public async Task DeleteCertificate_WhenNotFound_ThrowsMcpException()
    {
        var id = Guid.NewGuid();
        _service.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(false);

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().DeleteCertificate(id));

        ex.Message.ShouldContain(id.ToString());
        ex.Message.ShouldContain("was not found");
    }
}
