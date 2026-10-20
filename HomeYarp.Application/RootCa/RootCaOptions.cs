namespace HomeYarp.Application.RootCa;

public sealed class RootCaOptions
{
    public const string SectionName = "HomeYarp:RootCa";

    /// <summary>Common name of the generated root CA (only used when a CA is first created).</summary>
    public string CommonName { get; set; } = "HomeYarp Local Root CA";

    /// <summary>Validity of the generated root CA (only used when a CA is first created).</summary>
    public int ValidityDays { get; set; } = 5475; // ~15 years

    /// <summary>
    /// When true, the self-signed renewal worker re-issues every <c>Internal</c> certificate that was not
    /// signed by the current root CA (legacy per-cert self-signed certs). Leave off until the root CA has
    /// been installed on every client that previously trusted an individual leaf (CI runners, Docker
    /// hosts, Node trust bundles) — re-signing makes those pinned leaves stop validating.
    /// </summary>
    public bool MigrateLegacyCertificates { get; set; }
}
