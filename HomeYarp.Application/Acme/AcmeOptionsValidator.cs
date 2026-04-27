namespace HomeYarp.Application.Acme;

public static class AcmeOptionsValidator
{
    public static void EnsureConfigured(AcmeOptions options)
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("ACME is disabled. Set HomeYarp:Acme:Enabled to true to use Let's Encrypt.");
        }
        if (!options.AgreeToTermsOfService)
        {
            throw new InvalidOperationException("You must agree to the Let's Encrypt terms of service. Set HomeYarp:Acme:AgreeToTermsOfService to true.");
        }
        if (string.IsNullOrWhiteSpace(options.AccountEmail))
        {
            throw new InvalidOperationException("HomeYarp:Acme:AccountEmail is required.");
        }
        if (string.IsNullOrWhiteSpace(options.DirectoryUrl))
        {
            throw new InvalidOperationException("HomeYarp:Acme:DirectoryUrl is required.");
        }
    }

    public static bool IsConfigured(AcmeOptions options) =>
        options.Enabled
        && options.AgreeToTermsOfService
        && !string.IsNullOrWhiteSpace(options.AccountEmail)
        && !string.IsNullOrWhiteSpace(options.DirectoryUrl);
}
