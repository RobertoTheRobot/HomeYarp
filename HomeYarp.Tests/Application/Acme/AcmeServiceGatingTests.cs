using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Acme;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HomeYarp.Tests.Application.Acme;

/// <summary>
/// Unit tests for the gating paths in AcmeService — input validation, name uniqueness,
/// and "renew on a non-ACME cert throws". The full ACME order orchestration calls the
/// real Let's Encrypt directory via Certes and is covered by integration tests, not here.
/// </summary>
public class AcmeServiceGatingTests
{
    private readonly ICertificateRepository _certs = Substitute.For<ICertificateRepository>();
    private readonly IAcmeChallengeStore _challenges = Substitute.For<IAcmeChallengeStore>();
    private readonly IAcmeAccountStore _accounts = Substitute.For<IAcmeAccountStore>();
    private readonly IOptionsMonitor<AcmeOptions> _options = Substitute.For<IOptionsMonitor<AcmeOptions>>();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-15T10:00:00Z"));

    public AcmeServiceGatingTests()
    {
        _options.CurrentValue.Returns(Configured());
    }

    private static AcmeOptions Configured() => new()
    {
        Enabled = true,
        AgreeToTermsOfService = true,
        AccountEmail = "ops@example.test",
        DirectoryUrl = "https://example.test/directory"
    };

    private AcmeService Service => new(_certs, _challenges, _accounts, _options, _time);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task IssueAsync_WhenNameBlank_ThrowsArgumentException(string? name)
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            Service.IssueAsync(name!, null, new[] { "x.example.com" }));
    }

    [Fact]
    public async Task IssueAsync_WhenHostnamesNullOrEmpty_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            Service.IssueAsync("x", null, Array.Empty<string>()));
    }

    [Fact]
    public async Task IssueAsync_WhenHostnameWhitespace_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            Service.IssueAsync("x", null, new[] { "good.example.com", "  " }));
    }

    [Fact]
    public async Task IssueAsync_WhenWildcardHostname_ThrowsArgumentException()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            Service.IssueAsync("x", null, new[] { "*.example.com" }));
        ex.Message.ShouldContain("Wildcard", Case.Insensitive);
    }

    [Fact]
    public async Task IssueAsync_WhenNameAlreadyExists_ThrowsInvalidOperationException()
    {
        var existing = ApplicationFactory.CreateAcmeCert("dup", new[] { "x.example.com" });
        _certs.GetByNameAsync("dup", Arg.Any<CancellationToken>()).Returns(existing);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Service.IssueAsync("dup", null, new[] { "x.example.com" }));
        ex.Message.ShouldContain("already exists", Case.Insensitive);
    }

    [Fact]
    public async Task IssueAsync_WhenAcmeNotConfigured_ThrowsInvalidOperationException()
    {
        _certs.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);
        var disabled = Configured();
        disabled.Enabled = false;
        _options.CurrentValue.Returns(disabled);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Service.IssueAsync("x", null, new[] { "x.example.com" }));
    }

    [Fact]
    public async Task RenewAsync_WhenCertNotFound_ThrowsArgumentException()
    {
        _certs.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);

        await Should.ThrowAsync<ArgumentException>(() => Service.RenewAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task RenewAsync_WhenCertNotAcmeManaged_ThrowsInvalidOperation()
    {
        var selfSigned = ApplicationFactory.CreateSelfSignedCert("manual", new[] { "x.local" });
        _certs.GetByIdAsync(selfSigned.Id, Arg.Any<CancellationToken>()).Returns(selfSigned);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Service.RenewAsync(selfSigned.Id));
        ex.Message.ShouldContain("not ACME-managed", Case.Insensitive);
    }
}
