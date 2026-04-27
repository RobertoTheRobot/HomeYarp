namespace HomeYarp.Tests.Domain;

public class CertificateMutualExclusionTests
{
    [Fact]
    public void NewCertificate_HasNoAcmeOrSelfSigned_TreatedAsManualUpload()
    {
        var cert = new Certificate { Name = "manual" };

        cert.Acme.ShouldBeNull();
        cert.SelfSigned.ShouldBeNull();
    }

    [Fact]
    public void Certificate_DefaultsForCollectionsAndTimestamps_AreSet()
    {
        var cert = new Certificate { Name = "x" };

        cert.Id.ShouldNotBe(Guid.Empty);
        cert.SubjectAlternativeNames.ShouldBeEmpty();
        cert.Subject.ShouldBe(string.Empty);
        cert.Issuer.ShouldBe(string.Empty);
        cert.Thumbprint.ShouldBe(string.Empty);
        cert.CreatedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Certificate_AcmeAndSelfSigned_AreIndependentlyAssignable()
    {
        var cert = new Certificate
        {
            Name = "x",
            Acme = new AcmeMetadata { AccountEmail = "a@b.c" }
        };

        cert.Acme.ShouldNotBeNull();
        cert.SelfSigned.ShouldBeNull();

        // The domain doesn't enforce exclusivity at the type level — services treat
        // (Acme=null, SelfSigned=null) as manual upload. This test pins that contract.
        cert.SelfSigned = new SelfSignedMetadata();
        cert.Acme.ShouldNotBeNull();
        cert.SelfSigned.ShouldNotBeNull();
    }
}
