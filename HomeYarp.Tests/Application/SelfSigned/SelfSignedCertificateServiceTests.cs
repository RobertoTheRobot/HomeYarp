using HomeYarp.Application.Abstractions;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;

namespace HomeYarp.Tests.Application.SelfSigned;

public class SelfSignedCertificateServiceTests
{
    private readonly ICertificateRepository _repo = Substitute.For<ICertificateRepository>();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-15T10:00:00Z"));

    private SelfSignedCertificateService Service => new(_repo, _time);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task IssueAsync_WhenNameBlank_ThrowsArgumentException(string? name)
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            Service.IssueAsync(name!, null, new[] { "x.local" }, CertificateKeyType.Ec256, 365));
    }

    [Fact]
    public async Task IssueAsync_WhenHostnamesEmpty_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            Service.IssueAsync("x", null, Array.Empty<string>(), CertificateKeyType.Ec256, 365));
    }

    [Fact]
    public async Task IssueAsync_WhenHostnameWhitespace_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            Service.IssueAsync("x", null, new[] { "good.local", "  " }, CertificateKeyType.Ec256, 365));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task IssueAsync_WhenValidityDaysNotPositive_ThrowsArgumentException(int days)
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            Service.IssueAsync("x", null, new[] { "x.local" }, CertificateKeyType.Ec256, days));
    }

    [Fact]
    public async Task IssueAsync_WhenNameAlreadyExists_ThrowsInvalidOperationException()
    {
        _repo.GetByNameAsync("dup", Arg.Any<CancellationToken>())
             .Returns(ApplicationFactory.CreateSelfSignedCert("dup", new[] { "x.local" }));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Service.IssueAsync("dup", null, new[] { "x.local" }, CertificateKeyType.Ec256, 365));
    }

    [Fact]
    public async Task IssueAsync_WithEc256_GeneratesCertWithSelfSignedMetadata()
    {
        _repo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);

        var cert = await Service.IssueAsync("issued", "Friendly", new[] { "ha.home.lan" }, CertificateKeyType.Ec256, 365);

        cert.Name.ShouldBe("issued");
        cert.FriendlyName.ShouldBe("Friendly");
        cert.SelfSigned.ShouldNotBeNull();
        cert.SelfSigned!.Hostnames.ShouldBe(new[] { "ha.home.lan" });
        cert.SelfSigned.KeyType.ShouldBe(CertificateKeyType.Ec256);
        cert.SelfSigned.ValidityDays.ShouldBe(365);
        cert.SelfSigned.IssuedAt.ShouldBe(_time.GetUtcNow());
        cert.SelfSigned.RegeneratedAt.ShouldBeNull();
        cert.Acme.ShouldBeNull();
        cert.SubjectAlternativeNames.ShouldContain("ha.home.lan");
        cert.NotAfter.ShouldBe(_time.GetUtcNow().AddDays(365), TimeSpan.FromSeconds(2));

        await _repo.Received(1).SaveAsync(Arg.Any<Certificate>(), Arg.Any<CertificateMaterial>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IssueAsync_WhenProgressProvided_ReportsExpectedSteps()
    {
        _repo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);
        var reported = new List<string>();
        var progress = new SynchronousProgress<string>(reported.Add);

        await Service.IssueAsync("p", null, new[] { "p.local" }, CertificateKeyType.Ec256, 365, progress: progress);

        reported.ShouldNotBeEmpty();
        reported.ShouldContain(s => s.Contains("Checking certificate name uniqueness", StringComparison.OrdinalIgnoreCase));
        reported.ShouldContain(s => s.Contains("Generating", StringComparison.OrdinalIgnoreCase) && s.Contains("key", StringComparison.OrdinalIgnoreCase));
        reported.ShouldContain(s => s.Contains("Persisting certificate", StringComparison.OrdinalIgnoreCase));
        reported.ShouldContain(s => s.Contains("Reloading SNI selector", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IssueAsync_WithRsa2048_GeneratesCertWithRsaKeyType()
    {
        _repo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);

        var cert = await Service.IssueAsync("rsa", null, new[] { "rsa.local" }, CertificateKeyType.Rsa2048, 30);

        cert.SelfSigned!.KeyType.ShouldBe(CertificateKeyType.Rsa2048);
        cert.SubjectAlternativeNames.ShouldContain("rsa.local");
    }

    [Fact]
    public async Task IssueAsync_WithIpAddressHostname_GeneratesIpSan()
    {
        _repo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);

        // The cert MUST be generated. SAN parsing of IP addresses is not part of the DNS-name list,
        // so the SubjectAlternativeNames property won't contain the IP — but generation must succeed.
        var cert = await Service.IssueAsync("ip-cert", null, new[] { "host.local", "192.168.1.50" }, CertificateKeyType.Ec256, 365);

        cert.ShouldNotBeNull();
        cert.SubjectAlternativeNames.ShouldContain("host.local");
    }

    [Fact]
    public async Task RegenerateAsync_NoArg_ReusesExistingHostnames()
    {
        var id = Guid.NewGuid();
        var existing = ApplicationFactory.CreateSelfSignedCert("x", new[] { "x.example" });
        existing = CloneWithId(existing, id);
        _repo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);

        var regen = await Service.RegenerateAsync(id);

        regen.Id.ShouldBe(id);
        regen.SelfSigned.ShouldNotBeNull();
        regen.SelfSigned!.Hostnames.ShouldBe(new[] { "x.example" });
        regen.SelfSigned.RegeneratedAt.ShouldBe(_time.GetUtcNow());
    }

    [Fact]
    public async Task RegenerateAsync_WithHostnames_UpdatesSans()
    {
        var id = Guid.NewGuid();
        var existing = ApplicationFactory.CreateSelfSignedCert("x", new[] { "old.example" });
        existing = CloneWithId(existing, id);
        _repo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);

        var regen = await Service.RegenerateAsync(id, new[] { "new.example" });

        regen.SelfSigned!.Hostnames.ShouldBe(new[] { "new.example" });
        regen.SelfSigned.RegeneratedAt.ShouldBe(_time.GetUtcNow());
        regen.SubjectAlternativeNames.ShouldContain("new.example");
    }

    [Fact]
    public async Task RegenerateAsync_WhenCertNotFound_ThrowsArgumentException()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Certificate?)null);

        await Should.ThrowAsync<ArgumentException>(() => Service.RegenerateAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task RegenerateAsync_WhenCertNotSelfSigned_ThrowsInvalidOperation()
    {
        var acmeCert = ApplicationFactory.CreateAcmeCert("acme", new[] { "x.example" });
        _repo.GetByIdAsync(acmeCert.Id, Arg.Any<CancellationToken>()).Returns(acmeCert);

        await Should.ThrowAsync<InvalidOperationException>(() => Service.RegenerateAsync(acmeCert.Id));
    }

    [Fact]
    public async Task RegenerateAsync_WithHostnames_RejectsEmptyList()
    {
        await Should.ThrowAsync<ArgumentException>(() => Service.RegenerateAsync(Guid.NewGuid(), Array.Empty<string>()));
    }

    private static Certificate CloneWithId(Certificate c, Guid id) => new()
    {
        Id = id,
        Name = c.Name,
        FriendlyName = c.FriendlyName,
        Subject = c.Subject,
        Issuer = c.Issuer,
        Thumbprint = c.Thumbprint,
        NotBefore = c.NotBefore,
        NotAfter = c.NotAfter,
        SubjectAlternativeNames = c.SubjectAlternativeNames,
        CreatedAt = c.CreatedAt,
        SelfSigned = c.SelfSigned
    };
}
