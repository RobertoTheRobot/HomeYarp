using HomeYarp.Application.Acme;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Application.Services;
using HomeYarp.Tests.TestHelpers;
using HomeYarp.WebServer.Controllers;
using HomeYarp.WebServer.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

namespace HomeYarp.Tests.WebServer.Controllers;

public class CertificatesControllerTests
{
    private readonly ICertificateService _service = Substitute.For<ICertificateService>();
    private readonly IAcmeService _acme = Substitute.For<IAcmeService>();
    private readonly ISelfSignedCertificateService _selfSigned = Substitute.For<ISelfSignedCertificateService>();

    private CertificatesController NewController()
    {
        var controller = new CertificatesController(_service, _acme, _selfSigned);

        var services = new ServiceCollection();
        services.AddSingleton<ProblemDetailsFactory, FakeProblemDetailsFactory>();
        services.Configure<ApiBehaviorOptions>(o => { });
        var provider = services.BuildServiceProvider();

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = provider }
        };
        controller.ProblemDetailsFactory = provider.GetRequiredService<ProblemDetailsFactory>();
        return controller;
    }

    [Fact]
    public async Task List_ReturnsOkWithMappedDtos()
    {
        _service.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            ApplicationFactory.CreateSelfSignedCert("a", new[] { "x.local" })
        });

        var result = await NewController().List(CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_WhenMissing_Returns404()
    {
        _service.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);

        var result = await NewController().Get(Guid.NewGuid(), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Upload_OnSuccess_Returns201Created()
    {
        var cert = ApplicationFactory.CreateSelfSignedCert("ok", new[] { "x.local" });
        _service.UploadAsync("ok", null, Arg.Any<HomeYarp.Application.Abstractions.CertificateMaterial>(), Arg.Any<CancellationToken>())
                .Returns(cert);

        var request = new CertificateUploadRequest("ok", null, "cert", "key");
        var result = await NewController().Upload(request, CancellationToken.None);

        result.Result.ShouldBeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Upload_WhenArgumentException_ReturnsBadRequest()
    {
        _service.UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<HomeYarp.Application.Abstractions.CertificateMaterial>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new ArgumentException("bad pem"));

        var request = new CertificateUploadRequest("x", null, "cert", "key");
        var result = await NewController().Upload(request, CancellationToken.None);

        var objectResult = result.Result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Upload_WhenInvalidOp_ReturnsConflict()
    {
        _service.UploadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<HomeYarp.Application.Abstractions.CertificateMaterial>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("dup name"));

        var request = new CertificateUploadRequest("x", null, "cert", "key");
        var result = await NewController().Upload(request, CancellationToken.None);

        result.Result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task IssueViaAcme_OnSuccess_Returns201()
    {
        var cert = ApplicationFactory.CreateAcmeCert("acme-cert", new[] { "x.example.com" });
        _acme.IssueAsync("acme-cert", null, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
             .Returns(cert);

        var result = await NewController().IssueViaAcme(
            new AcmeIssueRequest("acme-cert", null, new[] { "x.example.com" }), CancellationToken.None);

        result.Result.ShouldBeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task IssueViaAcme_WhenInvalidOp_ReturnsConflict()
    {
        _acme.IssueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new InvalidOperationException("ACME disabled"));

        var result = await NewController().IssueViaAcme(
            new AcmeIssueRequest("x", null, new[] { "x.example.com" }), CancellationToken.None);

        result.Result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task IssueSelfSigned_OnSuccess_Returns201()
    {
        var cert = ApplicationFactory.CreateSelfSignedCert("self", new[] { "x.local" });
        _selfSigned.IssueAsync(
            "self",
            null,
            Arg.Any<IReadOnlyList<string>>(),
            CertificateKeyType.Ec256,
            365,
            Arg.Any<CancellationToken>()).Returns(cert);

        var result = await NewController().IssueSelfSigned(
            new SelfSignedIssueRequest("self", null, new[] { "x.local" }, null, null), CancellationToken.None);

        result.Result.ShouldBeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Regenerate_WhenArgumentException_ReturnsNotFound()
    {
        _selfSigned.RegenerateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                   .ThrowsAsync(new ArgumentException("no such cert"));

        var result = await NewController().Regenerate(Guid.NewGuid(), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Regenerate_WhenInvalidOp_ReturnsConflict()
    {
        _selfSigned.RegenerateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                   .ThrowsAsync(new InvalidOperationException("not self-signed"));

        var result = await NewController().Regenerate(Guid.NewGuid(), CancellationToken.None);

        result.Result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Renew_WhenArgumentException_ReturnsNotFound()
    {
        _acme.RenewAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new ArgumentException("no such cert"));

        var result = await NewController().Renew(Guid.NewGuid(), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Renew_WhenInvalidOp_ReturnsConflict()
    {
        _acme.RenewAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new InvalidOperationException("not acme"));

        var result = await NewController().Renew(Guid.NewGuid(), CancellationToken.None);

        result.Result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Delete_WhenRemoved_Returns204()
    {
        _service.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await NewController().Delete(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Delete_WhenMissing_Returns404()
    {
        _service.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await NewController().Delete(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    private sealed class FakeProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(HttpContext httpContext, int? statusCode = null, string? title = null, string? type = null, string? detail = null, string? instance = null)
            => new() { Status = statusCode ?? 400, Title = title, Detail = detail, Type = type, Instance = instance };

        public override ValidationProblemDetails CreateValidationProblemDetails(HttpContext httpContext, ModelStateDictionary modelStateDictionary, int? statusCode = null, string? title = null, string? type = null, string? detail = null, string? instance = null)
            => new(modelStateDictionary) { Status = statusCode ?? 400, Title = title, Detail = detail, Type = type, Instance = instance };
    }
}
