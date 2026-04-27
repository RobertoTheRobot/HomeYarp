namespace HomeYarp.Application.SelfSigned;

public sealed class SelfSignedOptions
{
    public const string SectionName = "HomeYarp:SelfSigned";

    public bool Enabled { get; set; } = true;

    public TimeSpan RenewBefore { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan RenewalInterval { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(1);
}
