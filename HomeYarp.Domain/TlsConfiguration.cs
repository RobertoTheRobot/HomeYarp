namespace HomeYarp.Domain;

public sealed class TlsConfiguration
{
    public TlsMode Mode { get; set; } = TlsMode.None;

    public Guid? CertificateId { get; set; }

    public TlsCertificateSource Source { get; set; } = TlsCertificateSource.Manual;
}
