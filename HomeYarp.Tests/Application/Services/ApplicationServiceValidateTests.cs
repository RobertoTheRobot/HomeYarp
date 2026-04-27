using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Acme;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Application.Services;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Options;

namespace HomeYarp.Tests.Application.Services;

public class ApplicationServiceValidateTests
{
    private static ApplicationService NewService(out IApplicationRepository repo, AcmeOptions? acme = null)
    {
        repo = Substitute.For<IApplicationRepository>();
        repo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((DomainApplication?)null);

        var certs = Substitute.For<ICertificateRepository>();
        var selfSigned = Substitute.For<ISelfSignedCertificateService>();
        var acmeService = Substitute.For<IAcmeService>();

        var monitor = Substitute.For<IOptionsMonitor<AcmeOptions>>();
        monitor.CurrentValue.Returns(acme ?? new AcmeOptions());
        return new ApplicationService(repo, certs, selfSigned, acmeService, monitor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task CreateAsync_WhenNameIsBlank_ThrowsArgumentException(string? name)
    {
        var service = NewService(out _);
        var app = ApplicationFactory.Create(name: name!, destinationAddress: "http://x:80");

        var ex = await Should.ThrowAsync<ArgumentException>(() => service.CreateAsync(app));
        ex.Message.ShouldContain("name is required", Case.Insensitive);
    }

    [Fact]
    public async Task CreateAsync_WhenClusterHasNoDestinations_ThrowsArgumentException()
    {
        var service = NewService(out _);
        var app = ApplicationFactory.Create();
        app.Cluster.Destinations.Clear();

        var ex = await Should.ThrowAsync<ArgumentException>(() => service.CreateAsync(app));
        ex.Message.ShouldContain("destination", Case.Insensitive);
    }

    [Fact]
    public async Task CreateAsync_WhenDestinationNameBlank_ThrowsArgumentException()
    {
        var service = NewService(out _);
        var app = ApplicationFactory.Create();
        app.Cluster.Destinations[0].Name = "";

        await Should.ThrowAsync<ArgumentException>(() => service.CreateAsync(app));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public async Task CreateAsync_WhenDestinationAddressNotAbsoluteUri_ThrowsArgumentException(string address)
    {
        var service = NewService(out _);
        var app = ApplicationFactory.Create(destinationAddress: address);

        var ex = await Should.ThrowAsync<ArgumentException>(() => service.CreateAsync(app));
        ex.Message.ShouldContain("invalid address", Case.Insensitive);
    }

    [Fact]
    public async Task CreateAsync_WhenSourceInternalAndModeNotOffload_ThrowsArgumentException()
    {
        var service = NewService(out _);
        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.None,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: new[] { "x.example" });

        var ex = await Should.ThrowAsync<ArgumentException>(() => service.CreateAsync(app));
        ex.Message.ShouldContain("Offload");
    }

    [Fact]
    public async Task CreateAsync_WhenSourceManualAndOffloadButNoCertId_ThrowsArgumentException()
    {
        var service = NewService(out _);
        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Manual,
            certificateId: null);

        var ex = await Should.ThrowAsync<ArgumentException>(() => service.CreateAsync(app));
        ex.Message.ShouldContain("CertificateId", Case.Insensitive);
    }

    [Fact]
    public async Task CreateAsync_WhenSourceInternalAndNoHostnames_ThrowsArgumentException()
    {
        var service = NewService(out _);
        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Internal,
            routeHosts: Array.Empty<string>());

        var ex = await Should.ThrowAsync<ArgumentException>(() => service.CreateAsync(app));
        ex.Message.ShouldContain("hostname", Case.Insensitive);
    }

    [Fact]
    public async Task CreateAsync_WhenSourceExternalAndWildcardHostname_ThrowsArgumentException()
    {
        var service = NewService(out _, acme: new AcmeOptions
        {
            Enabled = true,
            AgreeToTermsOfService = true,
            AccountEmail = "a@b.c",
            DirectoryUrl = "https://example.test/directory"
        });
        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.External,
            routeHosts: new[] { "*.example.com" });

        var ex = await Should.ThrowAsync<ArgumentException>(() => service.CreateAsync(app));
        ex.Message.ShouldContain("Wildcard", Case.Insensitive);
    }

    [Fact]
    public async Task CreateAsync_WhenSourceManualAndModeNoneAndNoCert_DoesNotThrow()
    {
        var service = NewService(out var repo);
        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.None,
            tlsSource: TlsCertificateSource.Manual);

        await service.CreateAsync(app);

        await repo.Received(1).AddAsync(Arg.Any<DomainApplication>(), Arg.Any<CancellationToken>());
    }
}
