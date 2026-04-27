namespace HomeYarp.Tests.TestHelpers;

public static class ApplicationFactory
{
    public static DomainApplication Create(
        string name = "test-app",
        string? displayName = null,
        bool enabled = true,
        string[]? routeHosts = null,
        string destinationAddress = "http://localhost:5000",
        TlsMode tlsMode = TlsMode.None,
        TlsCertificateSource tlsSource = TlsCertificateSource.Manual,
        Guid? certificateId = null)
    {
        var hosts = routeHosts ?? Array.Empty<string>();
        return new DomainApplication
        {
            Name = name,
            DisplayName = displayName,
            Enabled = enabled,
            Routes = new List<RouteDefinition>
            {
                new()
                {
                    Hosts = hosts.ToList(),
                    Path = "/{**catch-all}"
                }
            },
            Cluster = new ClusterDefinition
            {
                Destinations = new List<DestinationDefinition>
                {
                    new() { Name = "primary", Address = destinationAddress }
                }
            },
            Tls = new TlsConfiguration
            {
                Mode = tlsMode,
                Source = tlsSource,
                CertificateId = certificateId
            }
        };
    }

    public static Certificate CreateSelfSignedCert(string name, IEnumerable<string> hostnames)
    {
        var hosts = hostnames.ToList();
        return new Certificate
        {
            Name = name,
            Subject = $"CN={hosts.FirstOrDefault() ?? "test"}",
            Issuer = $"CN={hosts.FirstOrDefault() ?? "test"}",
            Thumbprint = Guid.NewGuid().ToString("N").ToUpperInvariant(),
            NotBefore = DateTimeOffset.UtcNow.AddMinutes(-5),
            NotAfter = DateTimeOffset.UtcNow.AddDays(365),
            SubjectAlternativeNames = hosts,
            SelfSigned = new SelfSignedMetadata
            {
                Hostnames = hosts,
                KeyType = CertificateKeyType.Ec256,
                ValidityDays = 365
            }
        };
    }

    public static Certificate CreateAcmeCert(string name, IEnumerable<string> hostnames, string accountEmail = "test@example.com", string directoryUrl = "https://example.test/directory")
    {
        var hosts = hostnames.ToList();
        return new Certificate
        {
            Name = name,
            Subject = $"CN={hosts.FirstOrDefault() ?? "test"}",
            Issuer = "CN=Fake LE",
            Thumbprint = Guid.NewGuid().ToString("N").ToUpperInvariant(),
            NotBefore = DateTimeOffset.UtcNow.AddMinutes(-5),
            NotAfter = DateTimeOffset.UtcNow.AddDays(90),
            SubjectAlternativeNames = hosts,
            Acme = new AcmeMetadata
            {
                Hostnames = hosts,
                AccountEmail = accountEmail,
                DirectoryUrl = directoryUrl,
                KeyType = AcmeKeyType.Ec256
            }
        };
    }
}
