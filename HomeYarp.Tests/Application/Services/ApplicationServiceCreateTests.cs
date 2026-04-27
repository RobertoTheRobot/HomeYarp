using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Acme;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Application.Services;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Options;

namespace HomeYarp.Tests.Application.Services;

public class ApplicationServiceCreateTests
{
    private readonly IApplicationRepository _repo = Substitute.For<IApplicationRepository>();
    private readonly ICertificateRepository _certs = Substitute.For<ICertificateRepository>();
    private readonly ISelfSignedCertificateService _selfSigned = Substitute.For<ISelfSignedCertificateService>();
    private readonly IAcmeService _acme = Substitute.For<IAcmeService>();
    private readonly IOptionsMonitor<AcmeOptions> _options = Substitute.For<IOptionsMonitor<AcmeOptions>>();

    public ApplicationServiceCreateTests()
    {
        _options.CurrentValue.Returns(new AcmeOptions());
        _repo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((DomainApplication?)null);
    }

    private ApplicationService Service => new(_repo, _certs, _selfSigned, _acme, _options);

    [Fact]
    public async Task CreateAsync_WhenNameAlreadyExists_ThrowsInvalidOperationException()
    {
        _repo.GetByNameAsync("dup", Arg.Any<CancellationToken>())
             .Returns(ApplicationFactory.Create(name: "dup"));
        var app = ApplicationFactory.Create(name: "dup");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Service.CreateAsync(app));
        ex.Message.ShouldContain("already exists", Case.Insensitive);
    }

    [Fact]
    public async Task CreateAsync_OnSuccess_PersistsToRepository()
    {
        var app = ApplicationFactory.Create(name: "new-app");

        var result = await Service.CreateAsync(app);

        await _repo.Received(1).AddAsync(app, Arg.Any<CancellationToken>());
        result.ShouldBeSameAs(app);
    }

    [Fact]
    public async Task CreateAsync_OnSuccess_StampsCreatedAndUpdatedAt()
    {
        var app = ApplicationFactory.Create(name: "stamps");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await Service.CreateAsync(app);

        var after = DateTimeOffset.UtcNow.AddSeconds(1);
        app.CreatedAt.ShouldBeInRange(before, after);
        app.UpdatedAt.ShouldBe(app.CreatedAt);
    }
}
