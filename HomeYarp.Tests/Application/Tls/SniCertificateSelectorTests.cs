using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Tls;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Primitives;

namespace HomeYarp.Tests.Application.Tls;

public class SniCertificateSelectorTests
{
    private readonly ICertificateRepository _certs = Substitute.For<ICertificateRepository>();
    private readonly IApplicationRepository _apps = Substitute.For<IApplicationRepository>();

    public SniCertificateSelectorTests()
    {
        // Default no-op change tokens unless overridden in a specific test.
        _certs.GetReloadToken().Returns(_ => NeverFiringToken());
        _apps.GetReloadToken().Returns(_ => NeverFiringToken());
        _certs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Certificate>());
        _apps.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<DomainApplication>());
    }

    private static IChangeToken NeverFiringToken()
    {
        var token = Substitute.For<IChangeToken>();
        token.HasChanged.Returns(false);
        token.ActiveChangeCallbacks.Returns(false);
        token.RegisterChangeCallback(Arg.Any<Action<object?>>(), Arg.Any<object?>())
             .Returns(NullDisposable.Instance);
        return token;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }

    private void SeedAppAndCert(string host, Guid certId)
    {
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem(host);
        var cert = new Certificate
        {
            Id = certId,
            Name = "test",
            SubjectAlternativeNames = new List<string> { host },
            NotBefore = DateTimeOffset.UtcNow.AddMinutes(-5),
            NotAfter = DateTimeOffset.UtcNow.AddYears(1)
        };
        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Manual,
            routeHosts: new[] { host },
            certificateId: certId);

        _apps.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { app });
        _certs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { cert });
        _certs.GetMaterialAsync(certId, Arg.Any<CancellationToken>())
              .Returns(new CertificateMaterial(certPem, keyPem));
    }

    [Fact]
    public void Select_WithNullSni_ReturnsNull()
    {
        using var selector = new SniCertificateSelector(_certs, _apps);

        selector.Select(null).ShouldBeNull();
    }

    [Fact]
    public void Select_WithEmptySni_ReturnsNull()
    {
        using var selector = new SniCertificateSelector(_certs, _apps);

        selector.Select(string.Empty).ShouldBeNull();
        selector.Select("   ").ShouldBeNull();
    }

    [Fact]
    public void Select_WithExactMatch_ReturnsCertificate()
    {
        SeedAppAndCert("ha.home.lan", Guid.NewGuid());
        using var selector = new SniCertificateSelector(_certs, _apps);

        var result = selector.Select("ha.home.lan");

        result.ShouldNotBeNull();
    }

    [Fact]
    public void Select_WithWildcardPattern_FallsBackToWildcardCert()
    {
        SeedAppAndCert("*.home.lan", Guid.NewGuid());
        using var selector = new SniCertificateSelector(_certs, _apps);

        var result = selector.Select("anything.home.lan");

        result.ShouldNotBeNull();
    }

    [Fact]
    public void Select_WhenNoMatchingHost_ReturnsNull()
    {
        SeedAppAndCert("ha.home.lan", Guid.NewGuid());
        using var selector = new SniCertificateSelector(_certs, _apps);

        selector.Select("unknown.home.lan").ShouldBeNull();
    }

    [Fact]
    public void Constructor_QueriesRepositoriesForInitialState()
    {
        SeedAppAndCert("ha.home.lan", Guid.NewGuid());

        using var selector = new SniCertificateSelector(_certs, _apps);

        _apps.Received().GetAllAsync(Arg.Any<CancellationToken>());
        _certs.Received().GetAllAsync(Arg.Any<CancellationToken>());
        // And the freshly-loaded cert is selectable.
        selector.Select("ha.home.lan").ShouldNotBeNull();
    }

    [Fact]
    public void Reload_WhenCertMaterialThrows_DoesNotPropagateAndKeepsPreviousBindings()
    {
        // Regression: reload exceptions used to propagate up through
        // ChangeToken.OnChange → CTS.Cancel() → the request that triggered the
        // save, leaving the on-disk file written but the in-memory cert map
        // half-swapped. Restart was the only recovery.
        var certs = Substitute.For<ICertificateRepository>();
        var apps = Substitute.For<IApplicationRepository>();

        var certId = Guid.NewGuid();
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem("ha.home.lan");
        var cert = new Certificate
        {
            Id = certId,
            Name = "test",
            SubjectAlternativeNames = new List<string> { "ha.home.lan" },
            NotBefore = DateTimeOffset.UtcNow.AddMinutes(-5),
            NotAfter = DateTimeOffset.UtcNow.AddYears(1)
        };
        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Manual,
            routeHosts: new[] { "ha.home.lan" },
            certificateId: certId);
        apps.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { app });
        certs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { cert });

        var materialCalls = 0;
        certs.GetMaterialAsync(certId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            materialCalls++;
            if (materialCalls == 1) return new CertificateMaterial(certPem, keyPem);
            throw new IOException("simulated disk read failure during reload");
        });

        var appsCts = new CancellationTokenSource();
        var certsToken = NeverFiringToken();
        apps.GetReloadToken().Returns(_ => new CancellationChangeToken(appsCts.Token));
        certs.GetReloadToken().Returns(certsToken);

        using var selector = new SniCertificateSelector(certs, apps);
        selector.Select("ha.home.lan").ShouldNotBeNull();

        // Swap CTS before cancel so the re-registration step in ChangeToken.OnChange
        // binds against the new (uncanceled) token instead of looping infinitely.
        var firing = appsCts;
        appsCts = new CancellationTokenSource();
        Should.NotThrow(() => firing.Cancel());

        // Reload threw; the previous good binding must still serve.
        selector.Select("ha.home.lan").ShouldNotBeNull();
    }
}
