using HomeYarp.Application.Services;
using HomeYarp.Tests.TestHelpers;
using HomeYarp.WebServer.Dtos;
using HomeYarp.WebServer.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace HomeYarp.Tests.WebServer.Mcp;

public class ApplicationToolsTests
{
    private readonly IApplicationService _service = Substitute.For<IApplicationService>();

    private ApplicationTools NewTools() =>
        new(_service, NullLogger<ApplicationTools>.Instance);

    private static ApplicationRequest BuildRequest(string name = "x") => new(
        Name: name,
        DisplayName: null,
        Description: null,
        Enabled: true,
        Routes: null,
        Cluster: new ClusterDto(null, new[] { new DestinationDto("primary", "http://localhost:5000", null) }),
        Tls: null,
        AuthorizationPolicy: null);

    // list_applications

    [Fact]
    public async Task ListApplications_ReturnsMappedResponses()
    {
        var apps = new List<DomainApplication>
        {
            ApplicationFactory.Create(name: "alpha"),
            ApplicationFactory.Create(name: "beta")
        };
        _service.ListAsync(Arg.Any<CancellationToken>()).Returns(apps);

        var result = await NewTools().ListApplications();

        result.Count.ShouldBe(2);
        result.ShouldContain(r => r.Name == "alpha");
        result.ShouldContain(r => r.Name == "beta");
    }

    [Fact]
    public async Task ListApplications_WhenEmpty_ReturnsEmptyList()
    {
        _service.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<DomainApplication>());

        var result = await NewTools().ListApplications();

        result.ShouldBeEmpty();
    }

    // get_application

    [Fact]
    public async Task GetApplication_WhenFound_ReturnsMappedResponse()
    {
        var app = ApplicationFactory.Create(name: "found");
        _service.GetAsync(app.Id, Arg.Any<CancellationToken>()).Returns(app);

        var result = await NewTools().GetApplication(app.Id);

        result.Name.ShouldBe("found");
        result.Id.ShouldBe(app.Id);
    }

    [Fact]
    public async Task GetApplication_WhenMissing_ThrowsMcpException()
    {
        _service.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((DomainApplication?)null);

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().GetApplication(Guid.NewGuid()));

        ex.Message.ShouldContain("was not found");
    }

    // create_application

    [Fact]
    public async Task CreateApplication_OnSuccess_ReturnsMappedResponse()
    {
        var app = ApplicationFactory.Create(name: "x");
        _service.CreateAsync(Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>()).Returns(app);

        var result = await NewTools().CreateApplication(BuildRequest("x"));

        result.Name.ShouldBe("x");
    }

    [Fact]
    public async Task CreateApplication_WhenArgumentException_ThrowsMcpExceptionWithValidationPrefix()
    {
        _service.CreateAsync(Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new ArgumentException("bad input"));

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().CreateApplication(BuildRequest()));

        ex.Message.ShouldStartWith("Validation failed:");
        ex.Message.ShouldContain("bad input");
    }

    [Fact]
    public async Task CreateApplication_WhenInvalidOperationException_ThrowsMcpExceptionWithOriginalMessage()
    {
        _service.CreateAsync(Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("name already in use"));

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().CreateApplication(BuildRequest()));

        ex.Message.ShouldBe("name already in use");
    }

    // update_application

    [Fact]
    public async Task UpdateApplication_OnSuccess_ReturnsMappedResponse()
    {
        var app = ApplicationFactory.Create(name: "x");
        _service.UpdateAsync(Arg.Any<Guid>(), Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>())
                .Returns(app);

        var result = await NewTools().UpdateApplication(app.Id, BuildRequest("x"));

        result.Name.ShouldBe("x");
    }

    [Fact]
    public async Task UpdateApplication_WhenKeyNotFound_ThrowsMcpExceptionNotFound()
    {
        _service.UpdateAsync(Arg.Any<Guid>(), Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new KeyNotFoundException());

        var id = Guid.NewGuid();
        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().UpdateApplication(id, BuildRequest()));

        ex.Message.ShouldContain(id.ToString());
        ex.Message.ShouldContain("was not found");
    }

    [Fact]
    public async Task UpdateApplication_WhenArgumentException_ThrowsMcpExceptionWithValidationPrefix()
    {
        _service.UpdateAsync(Arg.Any<Guid>(), Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new ArgumentException("destination required"));

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().UpdateApplication(Guid.NewGuid(), BuildRequest()));

        ex.Message.ShouldStartWith("Validation failed:");
    }

    [Fact]
    public async Task UpdateApplication_WhenInvalidOperationException_ThrowsMcpExceptionWithOriginalMessage()
    {
        _service.UpdateAsync(Arg.Any<Guid>(), Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("ACME not configured"));

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().UpdateApplication(Guid.NewGuid(), BuildRequest()));

        ex.Message.ShouldBe("ACME not configured");
    }

    // delete_application

    [Fact]
    public async Task DeleteApplication_WhenRemoved_ReturnsConfirmation()
    {
        var id = Guid.NewGuid();
        _service.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await NewTools().DeleteApplication(id);

        result.ShouldContain(id.ToString());
        result.ShouldContain("deleted");
    }

    [Fact]
    public async Task DeleteApplication_WhenNotFound_ThrowsMcpException()
    {
        var id = Guid.NewGuid();
        _service.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(false);

        var ex = await Should.ThrowAsync<McpException>(() =>
            NewTools().DeleteApplication(id));

        ex.Message.ShouldContain(id.ToString());
        ex.Message.ShouldContain("was not found");
    }
}
