using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HomeYarp.Application.RootCa;
using HomeYarp.Tests.TestHelpers;

namespace HomeYarp.Tests.Application.RootCa;

public class RootCaServiceTests
{
    [Fact]
    public async Task EnsureInitializedAsync_FirstRun_GeneratesAndPersistsCa()
    {
        var store = new InMemoryRootCaStore();
        var service = InMemoryRootCaStore.NewService(store);

        var ca = await service.EnsureInitializedAsync();

        store.SaveCount.ShouldBe(1);
        store.Material.ShouldNotBeNull();
        ca.Subject.ShouldBe("CN=HomeYarp Local Root CA");
        ca.NotAfter.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddYears(14));
        store.Material!.PrivateKeyPem.ShouldContain("PRIVATE KEY");
        ca.CertificatePem.ShouldNotContain("PRIVATE KEY");
    }

    [Fact]
    public async Task EnsureInitializedAsync_SecondInstanceWithSameStore_ReusesExistingCa()
    {
        var store = new InMemoryRootCaStore();
        var first = await InMemoryRootCaStore.NewService(store).EnsureInitializedAsync();
        var second = await InMemoryRootCaStore.NewService(store).EnsureInitializedAsync();

        store.SaveCount.ShouldBe(1);
        second.Thumbprint.ShouldBe(first.Thumbprint);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ConcurrentCalls_GenerateOnlyOnce()
    {
        var store = new InMemoryRootCaStore();
        var service = InMemoryRootCaStore.NewService(store);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.EnsureInitializedAsync()));

        store.SaveCount.ShouldBe(1);
        results.Select(r => r.Thumbprint).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Ca_IsConstrainedToSigningLeafCertificates()
    {
        var service = InMemoryRootCaStore.NewService();
        using var ca = X509Certificate2.CreateFromPem(await service.GetRootCertificatePemAsync());

        var basic = ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        basic.CertificateAuthority.ShouldBeTrue();
        basic.HasPathLengthConstraint.ShouldBeTrue();
        basic.PathLengthConstraint.ShouldBe(0);
        basic.Critical.ShouldBeTrue();

        var usage = ca.Extensions.OfType<X509KeyUsageExtension>().Single();
        usage.KeyUsages.ShouldBe(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign);
        usage.Critical.ShouldBeTrue();
    }

    [Fact]
    public async Task GetRootCertificatePemAsync_NeverContainsPrivateKey()
    {
        var pem = await InMemoryRootCaStore.NewService().GetRootCertificatePemAsync();

        pem.ShouldContain("BEGIN CERTIFICATE");
        pem.ShouldNotContain("PRIVATE");
    }

    [Fact]
    public async Task SignLeafAsync_ProducesLeafThatChainsToRoot_WithAuthorityKeyIdentifierAndUniqueSerials()
    {
        var service = InMemoryRootCaStore.NewService();
        using var root = X509Certificate2.CreateFromPem(await service.GetRootCertificatePemAsync());
        var now = DateTimeOffset.UtcNow;

        X509Certificate2 Sign(string host)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            return service.SignLeafAsync(req, now.AddMinutes(-5), now.AddDays(90)).GetAwaiter().GetResult();
        }

        using var a = Sign("a.home.lan");
        using var b = Sign("b.home.lan");

        a.Issuer.ShouldBe(root.Subject);
        a.SerialNumber.ShouldNotBe(b.SerialNumber);
        a.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().ShouldHaveSingleItem();

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(a).ShouldBeTrue(string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
        chain.ChainElements.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SignLeafAsync_ClampsLeafExpiryToCaExpiry()
    {
        var service = InMemoryRootCaStore.NewService(options: new RootCaOptions { ValidityDays = 30 });
        var now = DateTimeOffset.UtcNow;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=long.home.lan", key, HashAlgorithmName.SHA256);

        using var leaf = await service.SignLeafAsync(req, now.AddMinutes(-5), now.AddDays(3650));
        var ca = await service.EnsureInitializedAsync();

        new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero).ShouldBeLessThanOrEqualTo(ca.NotAfter);
    }

    [Fact]
    public async Task EnsureInitializedAsync_UsesConfiguredCommonName()
    {
        var service = InMemoryRootCaStore.NewService(options: new RootCaOptions { CommonName = "Roberto Lab Root" });

        (await service.EnsureInitializedAsync()).Subject.ShouldBe("CN=Roberto Lab Root");
    }
}
