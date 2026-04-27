using HomeYarp.Domain;

namespace HomeYarp.Application.Acme;

public sealed class AcmeOptions
{
    public const string SectionName = "HomeYarp:Acme";

    public const string LetsEncryptProductionDirectory = "https://acme-v02.api.letsencrypt.org/directory";

    public const string LetsEncryptStagingDirectory = "https://acme-staging-v02.api.letsencrypt.org/directory";

    public bool Enabled { get; set; }

    public string AccountEmail { get; set; } = string.Empty;

    public bool AgreeToTermsOfService { get; set; }

    public string DirectoryUrl { get; set; } = LetsEncryptProductionDirectory;

    public AcmeKeyType KeyType { get; set; } = AcmeKeyType.Ec256;

    public TimeSpan RenewBefore { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan RenewalInterval { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(1);
}
