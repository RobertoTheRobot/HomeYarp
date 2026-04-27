using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Acme;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Application.Services;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Options;

namespace HomeYarp.Tests.Application.Services;

public class ApplicationServiceUpdateTests
{
    private readonly IApplicationRepository _repo = Substitute.For<IApplicationRepository>();
    private readonly ICertificateRepository _certs = Substitute.For<ICertificateRepository>();
    private readonly ISelfSignedCertificateService _selfSigned = Substitute.For<ISelfSignedCertificateService>();
    private readonly IAcmeService _acme = Substitute.For<IAcmeService>();
    private readonly IOptionsMonitor<AcmeOptions> _options = Substitute.For<IOptionsMonitor<AcmeOptions>>();

    public ApplicationServiceUpdateTests()
    {
        _options.CurrentValue.Returns(new AcmeOptions());
        _repo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((DomainApplication?)null);
    }

    private ApplicationService Service => new(_repo, _certs, _selfSigned, _acme, _options);

    [Fact]
    public async Task UpdateAsync_WhenIdNotFound_ThrowsKeyNotFoundException()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((DomainApplication?)null);
        var app = ApplicationFactory.Create();

        await Should.ThrowAsync<KeyNotFoundException>(() => Service.UpdateAsync(Guid.NewGuid(), app));
    }

    [Fact]
    public async Task UpdateAsync_WhenAnotherAppOwnsTheName_ThrowsInvalidOperationException()
    {
        var existing = ApplicationFactory.Create(name: "old");
        var conflicting = ApplicationFactory.Create(name: "duplicate");

        _repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _repo.GetByNameAsync("duplicate", Arg.Any<CancellationToken>()).Returns(conflicting);

        var incoming = ApplicationFactory.Create(name: "duplicate");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Service.UpdateAsync(existing.Id, incoming));
        ex.Message.ShouldContain("already exists", Case.Insensitive);
    }

    [Fact]
    public async Task UpdateAsync_WhenSameNameOwnedByThisRow_DoesNotThrow()
    {
        var existing = ApplicationFactory.Create(name: "same");
        _repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _repo.GetByNameAsync("same", Arg.Any<CancellationToken>()).Returns(existing);

        var incoming = ApplicationFactory.Create(name: "same");
        incoming.DisplayName = "Same App Updated";

        var result = await Service.UpdateAsync(existing.Id, incoming);

        result.DisplayName.ShouldBe("Same App Updated");
    }

    [Fact]
    public async Task UpdateAsync_OnSuccess_CopiesFieldsAndStampsUpdatedAt()
    {
        var existing = ApplicationFactory.Create(name: "x");
        var originalCreated = existing.CreatedAt;
        _repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        var incoming = ApplicationFactory.Create(name: "x");
        incoming.DisplayName = "New Display";
        incoming.Description = "New Desc";
        incoming.Enabled = false;
        incoming.AuthorizationPolicy = "policy-x";

        await Task.Delay(5);
        var result = await Service.UpdateAsync(existing.Id, incoming);

        result.ShouldBeSameAs(existing);
        result.DisplayName.ShouldBe("New Display");
        result.Description.ShouldBe("New Desc");
        result.Enabled.ShouldBeFalse();
        result.AuthorizationPolicy.ShouldBe("policy-x");
        result.CreatedAt.ShouldBe(originalCreated);
        result.UpdatedAt.ShouldBeGreaterThan(originalCreated);
        await _repo.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
    }
}
