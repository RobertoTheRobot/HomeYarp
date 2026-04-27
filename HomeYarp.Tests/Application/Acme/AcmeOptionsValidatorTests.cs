using HomeYarp.Application.Acme;

namespace HomeYarp.Tests.Application.Acme;

public class AcmeOptionsValidatorTests
{
    private static AcmeOptions Configured() => new()
    {
        Enabled = true,
        AgreeToTermsOfService = true,
        AccountEmail = "ops@example.test",
        DirectoryUrl = "https://example.test/directory"
    };

    [Fact]
    public void EnsureConfigured_WhenEnabledFalse_Throws()
    {
        var opts = Configured();
        opts.Enabled = false;

        var ex = Should.Throw<InvalidOperationException>(() => AcmeOptionsValidator.EnsureConfigured(opts));
        ex.Message.ShouldContain("Enabled");
    }

    [Fact]
    public void EnsureConfigured_WhenAgreeToTermsFalse_Throws()
    {
        var opts = Configured();
        opts.AgreeToTermsOfService = false;

        var ex = Should.Throw<InvalidOperationException>(() => AcmeOptionsValidator.EnsureConfigured(opts));
        ex.Message.ShouldContain("terms of service", Case.Insensitive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureConfigured_WhenAccountEmailBlank_Throws(string email)
    {
        var opts = Configured();
        opts.AccountEmail = email;

        var ex = Should.Throw<InvalidOperationException>(() => AcmeOptionsValidator.EnsureConfigured(opts));
        ex.Message.ShouldContain("AccountEmail");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureConfigured_WhenDirectoryUrlBlank_Throws(string url)
    {
        var opts = Configured();
        opts.DirectoryUrl = url;

        var ex = Should.Throw<InvalidOperationException>(() => AcmeOptionsValidator.EnsureConfigured(opts));
        ex.Message.ShouldContain("DirectoryUrl");
    }

    [Fact]
    public void EnsureConfigured_WhenAllSettingsValid_DoesNotThrow()
    {
        Should.NotThrow(() => AcmeOptionsValidator.EnsureConfigured(Configured()));
    }

    [Fact]
    public void IsConfigured_OnlyTrue_WhenAllFourPredicatesPass()
    {
        AcmeOptionsValidator.IsConfigured(Configured()).ShouldBeTrue();

        var notEnabled = Configured(); notEnabled.Enabled = false;
        AcmeOptionsValidator.IsConfigured(notEnabled).ShouldBeFalse();

        var notAgreed = Configured(); notAgreed.AgreeToTermsOfService = false;
        AcmeOptionsValidator.IsConfigured(notAgreed).ShouldBeFalse();

        var noEmail = Configured(); noEmail.AccountEmail = "";
        AcmeOptionsValidator.IsConfigured(noEmail).ShouldBeFalse();

        var noUrl = Configured(); noUrl.DirectoryUrl = "";
        AcmeOptionsValidator.IsConfigured(noUrl).ShouldBeFalse();
    }
}
