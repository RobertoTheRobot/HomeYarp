namespace HomeYarp.Tests.Domain;

public class EnumDefaultsTests
{
    [Fact]
    public void TlsMode_HasExpectedValues()
    {
        ((int)TlsMode.None).ShouldBe(0);
        ((int)TlsMode.Offload).ShouldBe(1);
        ((int)TlsMode.Passthrough).ShouldBe(2);
    }

    [Fact]
    public void TlsCertificateSource_HasExpectedValues()
    {
        ((int)TlsCertificateSource.Manual).ShouldBe(0);
        ((int)TlsCertificateSource.Internal).ShouldBe(1);
        ((int)TlsCertificateSource.External).ShouldBe(2);
    }

    [Fact]
    public void AcmeKeyType_HasExpectedValues()
    {
        ((int)AcmeKeyType.Ec256).ShouldBe(0);
        ((int)AcmeKeyType.Rsa2048).ShouldBe(1);
    }

    [Fact]
    public void CertificateKeyType_HasExpectedValues()
    {
        ((int)CertificateKeyType.Ec256).ShouldBe(0);
        ((int)CertificateKeyType.Rsa2048).ShouldBe(1);
    }

    [Fact]
    public void TlsConfiguration_DefaultsToNoneAndManual()
    {
        var tls = new TlsConfiguration();

        tls.Mode.ShouldBe(TlsMode.None);
        tls.Source.ShouldBe(TlsCertificateSource.Manual);
        tls.CertificateId.ShouldBeNull();
    }

    [Fact]
    public void SelfSignedMetadata_DefaultsToEc256AndYearLong()
    {
        var meta = new SelfSignedMetadata();

        meta.KeyType.ShouldBe(CertificateKeyType.Ec256);
        meta.ValidityDays.ShouldBe(365);
        meta.Hostnames.ShouldBeEmpty();
        meta.RegeneratedAt.ShouldBeNull();
    }

    [Fact]
    public void AcmeMetadata_DefaultsToEc256()
    {
        var meta = new AcmeMetadata();

        meta.KeyType.ShouldBe(AcmeKeyType.Ec256);
        meta.Hostnames.ShouldBeEmpty();
        meta.AccountEmail.ShouldBe(string.Empty);
        meta.DirectoryUrl.ShouldBe(string.Empty);
        meta.RenewedAt.ShouldBeNull();
    }
}
