using HomeYarp.Application.Services;
using HomeYarp.Tests.TestHelpers;
using HomeYarp.WebServer.Controllers;
using HomeYarp.WebServer.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HomeYarp.Tests.WebServer.Controllers;

public class ApplicationsControllerTests
{
    private readonly IApplicationService _service = Substitute.For<IApplicationService>();

    private ApplicationsController NewController()
    {
        var controller = new ApplicationsController(_service);

        // ValidationProblem() needs a ProblemDetailsFactory in the request services.
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

    private static ApplicationRequest BuildRequest(string name = "x") => new(
        Name: name,
        DisplayName: null,
        Description: null,
        Enabled: true,
        Routes: null,
        Cluster: new ClusterDto(null, new[] { new DestinationDto("primary", "http://localhost:5000", null) }),
        Tls: null,
        AuthorizationPolicy: null);

    [Fact]
    public async Task List_ReturnsOkWithMappedDtos()
    {
        var apps = new List<DomainApplication>
        {
            ApplicationFactory.Create(name: "alpha"),
            ApplicationFactory.Create(name: "beta")
        };
        _service.ListAsync(Arg.Any<CancellationToken>()).Returns(apps);

        var controller = NewController();
        var result = await controller.List(CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var responses = ok.Value.ShouldBeAssignableTo<IReadOnlyList<ApplicationResponse>>();
        responses!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Get_WhenAppFound_ReturnsOk()
    {
        var app = ApplicationFactory.Create(name: "found");
        _service.GetAsync(app.Id, Arg.Any<CancellationToken>()).Returns(app);

        var controller = NewController();
        var result = await controller.Get(app.Id, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_WhenAppMissing_ReturnsNotFound()
    {
        _service.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((DomainApplication?)null);

        var controller = NewController();
        var result = await controller.Get(Guid.NewGuid(), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_OnSuccess_Returns201Created()
    {
        var app = ApplicationFactory.Create(name: "x");
        _service.CreateAsync(Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>()).Returns(app);

        var controller = NewController();
        var result = await controller.Create(BuildRequest(), CancellationToken.None);

        result.Result.ShouldBeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_WhenServiceThrowsArgumentException_ReturnsBadRequest()
    {
        _service.CreateAsync(Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new ArgumentException("bad input"));

        var controller = NewController();
        var result = await controller.Create(BuildRequest(), CancellationToken.None);

        var objectResult = result.Result.ShouldBeAssignableTo<ObjectResult>();
        objectResult!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_WhenServiceThrowsInvalidOperationException_ReturnsConflict()
    {
        _service.CreateAsync(Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("name in use"));

        var controller = NewController();
        var result = await controller.Create(BuildRequest(), CancellationToken.None);

        result.Result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Update_WhenKeyNotFound_ReturnsNotFound()
    {
        _service.UpdateAsync(Arg.Any<Guid>(), Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new KeyNotFoundException());

        var controller = NewController();
        var result = await controller.Update(Guid.NewGuid(), BuildRequest(), CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_OnSuccess_ReturnsOk()
    {
        var app = ApplicationFactory.Create(name: "x");
        _service.UpdateAsync(Arg.Any<Guid>(), Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>())
                .Returns(app);

        var controller = NewController();
        var result = await controller.Update(app.Id, BuildRequest(), CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Update_WhenInvalidOp_ReturnsConflict()
    {
        _service.UpdateAsync(Arg.Any<Guid>(), Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("conflict"));

        var controller = NewController();
        var result = await controller.Update(Guid.NewGuid(), BuildRequest(), CancellationToken.None);

        result.Result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Delete_WhenRemoved_ReturnsNoContent()
    {
        _service.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        var controller = NewController();
        var result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Delete_WhenNotFound_Returns404()
    {
        _service.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        var controller = NewController();
        var result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);

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
