using System.Security.Cryptography.X509Certificates;
using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Tls;
using HomeYarp.Tests.TestHelpers;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;

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
        _certs.WithCerts();
        _apps.WithApps();
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

        _apps.WithApps(app);
        _certs.WithCerts(cert);
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

        _apps.Received().GetSnapshot();
        _certs.Received().GetSnapshot();
        // And the freshly-loaded cert is selectable.
        selector.Select("ha.home.lan").ShouldNotBeNull();
    }

    [Fact]
    public void Reload_WhenCertThumbprintUnchanged_DoesNotReadMaterialAgain()
    {
        // Phase 2: cache by id+thumbprint. Second reload over the same cert metadata
        // must not re-call GetMaterialAsync (the disk read + PEM→PFX roundtrip).
        var (certs, apps, certId, _) = NewIsolatedSubstitutesWithSingleCert(out var appsCts);

        using var selector = new SniCertificateSelector(certs, apps);
        // Initial load read the material once.
        certs.Received(1).GetMaterialAsync(certId, Arg.Any<CancellationToken>());

        // Trigger a reload — same cert, same thumbprint.
        FireApps(apps, ref appsCts);

        // Cache hit — material was NOT read a second time.
        certs.Received(1).GetMaterialAsync(certId, Arg.Any<CancellationToken>());
        selector.Select("ha.home.lan").ShouldNotBeNull();
    }

    [Fact]
    public void Reload_WhenCertThumbprintChanges_ReadsMaterialAgain()
    {
        // Phase 2: a thumbprint change (renewal, regeneration, or upload of a different cert
        // with the same id) must invalidate the cache and force a fresh disk read.
        var (certs, apps, certId, host) = NewIsolatedSubstitutesWithSingleCert(out var appsCts);

        using var selector = new SniCertificateSelector(certs, apps);
        certs.Received(1).GetMaterialAsync(certId, Arg.Any<CancellationToken>());

        // Same id, different actual cert (fresh keypair → fresh thumbprint) → simulates a renew/regen.
        var (newCertPem, newKeyPem) = CertificateFactory.GenerateSelfSignedPem(host);
        using var newX509 = X509Certificate2.CreateFromPem(newCertPem, newKeyPem);
        var rotated = new Certificate
        {
            Id = certId,
            Name = "test",
            Thumbprint = newX509.Thumbprint,
            SubjectAlternativeNames = new List<string> { host },
            NotBefore = DateTimeOffset.UtcNow.AddMinutes(-5),
            NotAfter = DateTimeOffset.UtcNow.AddYears(1)
        };
        certs.WithCerts(rotated);
        certs.GetMaterialAsync(certId, Arg.Any<CancellationToken>())
             .Returns(new CertificateMaterial(newCertPem, newKeyPem));

        FireApps(apps, ref appsCts);

        // Material WAS read a second time because the cached thumbprint didn't match.
        certs.Received(2).GetMaterialAsync(certId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Reload_WhenAppRemoved_OrphanedCertNotImmediatelyDisposed()
    {
        // Phase 2 (deferred disposal): when a cert becomes orphaned (no app references it),
        // it must NOT be disposed immediately — Schannel may still hold the reference for
        // in-flight handshakes.
        var time = new FakeTimeProvider();
        var (certs, apps, _, _) = NewIsolatedSubstitutesWithSingleCert(out var appsCts);
        // Keep the selector alive for the duration of the test (Dispose would cancel deferred timers).
        var selector = new SniCertificateSelector(certs, apps, timeProvider: time);
        _selectorsToKeepAlive.Add(selector);
        var captured = selector.Select("ha.home.lan");
        captured.ShouldNotBeNull();

        // Remove the app — its cert becomes orphaned.
        apps.WithApps();
        FireApps(apps, ref appsCts);

        // Cert handle still alive — disposal is deferred.
        captured.Handle.ShouldNotBe(IntPtr.Zero);
    }

    [Fact]
    public void Reload_WhenAppRemoved_OrphanedCertDisposedAfterDelay()
    {
        var time = new FakeTimeProvider();
        var (certs, apps, _, _) = NewIsolatedSubstitutesWithSingleCert(out var appsCts);
        var selector = new SniCertificateSelector(certs, apps, timeProvider: time);
        _selectorsToKeepAlive.Add(selector);
        var captured = selector.Select("ha.home.lan");
        captured.ShouldNotBeNull();

        apps.WithApps();
        FireApps(apps, ref appsCts);

        // Advance past the 60s deferred-dispose delay.
        time.Advance(TimeSpan.FromSeconds(61));

        captured.Handle.ShouldBe(IntPtr.Zero);
    }

    private readonly List<SniCertificateSelector> _selectorsToKeepAlive = new();

    private static (ICertificateRepository Certs, IApplicationRepository Apps, Guid CertId, string Host) NewIsolatedSubstitutesWithSingleCert(
        out CancellationTokenSource appsCts,
        string host = "ha.home.lan")
    {
        // Fresh substitutes per test — avoids the constructor's NeverFiringToken setup,
        // whose Returns(_ => Substitute.For<...>()) lambda interferes with rebinding GetReloadToken
        // (NSubstitute disallows substitute creation inside an active Returns configuration).
        var certs = Substitute.For<ICertificateRepository>();
        var apps = Substitute.For<IApplicationRepository>();
        var certId = Guid.NewGuid();
        var (certPem, keyPem) = CertificateFactory.GenerateSelfSignedPem(host);
        // Production code stores the actual cert thumbprint in the manifest (CertificateService.UploadAsync etc.)
        // The selector's cache compares manifest.Thumbprint to the loaded X509's Thumbprint, so they must match.
        using var probe = X509Certificate2.CreateFromPem(certPem, keyPem);
        var cert = new Certificate
        {
            Id = certId,
            Name = "test",
            Thumbprint = probe.Thumbprint,
            SubjectAlternativeNames = new List<string> { host },
            NotBefore = DateTimeOffset.UtcNow.AddMinutes(-5),
            NotAfter = DateTimeOffset.UtcNow.AddYears(1)
        };
        var app = ApplicationFactory.Create(
            tlsMode: TlsMode.Offload,
            tlsSource: TlsCertificateSource.Manual,
            routeHosts: new[] { host },
            certificateId: certId);

        appsCts = new CancellationTokenSource();
        var capturedCts = appsCts;
        apps.GetReloadToken().Returns(_ => new CancellationChangeToken(capturedCts.Token));
        certs.GetReloadToken().Returns(_ => new CancellationChangeToken(new CancellationTokenSource().Token));
        apps.WithApps(app);
        certs.WithCerts(cert);
        certs.GetMaterialAsync(certId, Arg.Any<CancellationToken>())
             .Returns(new CertificateMaterial(certPem, keyPem));

        return (certs, apps, certId, host);
    }

    private static void FireApps(IApplicationRepository apps, ref CancellationTokenSource cts)
    {
        var firing = cts;
        cts = new CancellationTokenSource();
        var captured = cts;
        apps.GetReloadToken().Returns(_ => new CancellationChangeToken(captured.Token));
        firing.Cancel();
    }

    [Fact]
    public async Task Select_ParallelReadersDuringReload_NeverThrowAndAlwaysReturnConsistentSnapshot()
    {
        // Phase 3: Select is now lock-free (single volatile read of _byHost). Stress it with
        // many concurrent handshake threads while the writer thread fires reload events.
        // Acceptance: no exceptions; every non-null result is a real loaded cert (not a torn dict).
        var (certs, apps, _, host) = NewIsolatedSubstitutesWithSingleCert(out var appsCts);
        using var selector = new SniCertificateSelector(certs, apps);

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                FireApps(apps, ref appsCts);
            }
        });

        const int readerCount = 8;
        var readers = Enumerable.Range(0, readerCount).Select(i => Task.Run(() =>
        {
            var hits = 0;
            var misses = 0;
            while (!stop.IsCancellationRequested)
            {
                var result = selector.Select(host);
                if (result is not null)
                {
                    // Touch the cert to verify it's a real, alive instance — would throw if torn.
                    var probe = result.Thumbprint;
                    hits++;
                }
                else
                {
                    misses++;
                }
            }
            return (hits, misses);
        })).ToArray();

        await writer;
        var results = await Task.WhenAll(readers);

        // Sanity: every reader did at least *some* selects and didn't crash.
        foreach (var (hits, misses) in results)
        {
            (hits + misses).ShouldBeGreaterThan(0);
        }
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
        apps.WithApps(app);
        certs.WithCerts(cert);

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
