using System.Security.Cryptography.X509Certificates;
using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace HomeYarp.Application.Tls;

public sealed class SniCertificateSelector : IDisposable
{
    private readonly ICertificateRepository _certificates;
    private readonly IApplicationRepository _applications;
    private readonly ILogger<SniCertificateSelector> _logger;
    private readonly object _lock = new();

    private Dictionary<string, X509Certificate2> _byHost = new(StringComparer.OrdinalIgnoreCase);
    private List<X509Certificate2> _loaded = new();
    private IDisposable? _certSubscription;
    private IDisposable? _appSubscription;

    public SniCertificateSelector(ICertificateRepository certificates, IApplicationRepository applications, ILogger<SniCertificateSelector>? logger = null)
    {
        _certificates = certificates;
        _applications = applications;
        _logger = logger ?? NullLogger<SniCertificateSelector>.Instance;
        Reload();
        _certSubscription = ChangeToken.OnChange(_certificates.GetReloadToken, Reload);
        _appSubscription = ChangeToken.OnChange(_applications.GetReloadToken, Reload);
    }

    public X509Certificate2? Select(string? sni)
    {
        Dictionary<string, X509Certificate2> snapshot;
        lock (_lock)
        {
            snapshot = _byHost;
        }

        if (string.IsNullOrWhiteSpace(sni))
        {
            _logger.LogDebug("SNI selector: empty SNI on TLS handshake — no cert returned");
            return null;
        }

        if (snapshot.TryGetValue(sni, out var exact))
        {
            _logger.LogDebug("SNI selector: exact match for '{Sni}' (thumbprint {Thumbprint})", sni, exact.Thumbprint);
            return exact;
        }

        var dot = sni.IndexOf('.');
        if (dot > 0)
        {
            var wildcard = "*." + sni[(dot + 1)..];
            if (snapshot.TryGetValue(wildcard, out var wild))
            {
                _logger.LogDebug("SNI selector: wildcard '{Wildcard}' matched '{Sni}' (thumbprint {Thumbprint})", wildcard, sni, wild.Thumbprint);
                return wild;
            }
        }

        _logger.LogWarning(
            "SNI selector: no certificate for SNI '{Sni}'. Known hosts: [{Hosts}]",
            sni,
            string.Join(",", snapshot.Keys));
        return null;
    }

    private void Reload()
    {
        _logger.LogDebug("SNI selector: reload triggered (apps or certs changed)");

        var apps = _applications.GetAllAsync().GetAwaiter().GetResult();
        var certs = _certificates.GetAllAsync().GetAwaiter().GetResult();

        var certById = new Dictionary<Guid, Certificate>();
        foreach (var c in certs)
        {
            certById[c.Id] = c;
        }

        var loaded = new Dictionary<Guid, X509Certificate2>();
        var byHost = new Dictionary<string, X509Certificate2>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in apps)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["AppId"] = app.Id,
                ["AppName"] = app.Name
            });

            if (!app.Enabled || app.Tls.Mode != TlsMode.Offload || app.Tls.CertificateId is null)
            {
                continue;
            }

            if (!certById.TryGetValue(app.Tls.CertificateId.Value, out var certMeta))
            {
                _logger.LogWarning(
                    "SNI selector: app '{AppName}' ({AppId}) references missing certificate {CertId}",
                    app.Name,
                    app.Id,
                    app.Tls.CertificateId);
                continue;
            }

            if (!loaded.TryGetValue(certMeta.Id, out var loadedCert))
            {
                var material = _certificates.GetMaterialAsync(certMeta.Id).GetAwaiter().GetResult();
                if (material is null)
                {
                    _logger.LogWarning(
                        "SNI selector: certificate {CertId} ({CertName}) for app '{AppName}' ({AppId}) has no PEM material on disk",
                        certMeta.Id,
                        certMeta.Name,
                        app.Name,
                        app.Id);
                    continue;
                }

                try
                {
                    loadedCert = LoadX509(material);
                    loaded[certMeta.Id] = loadedCert;
                    _logger.LogDebug(
                        "SNI selector: loaded cert {CertId} ({CertName}) thumbprint={Thumbprint} notAfter={NotAfter:o}",
                        certMeta.Id,
                        certMeta.Name,
                        loadedCert.Thumbprint,
                        loadedCert.NotAfter);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "SNI selector: failed to load cert {CertId} ({CertName}) for app '{AppName}' ({AppId})",
                        certMeta.Id,
                        certMeta.Name,
                        app.Name,
                        app.Id);
                    continue;
                }
            }

            foreach (var route in app.Routes)
            {
                foreach (var host in route.Hosts)
                {
                    if (!string.IsNullOrWhiteSpace(host))
                    {
                        byHost[host] = loadedCert;
                        _logger.LogDebug(
                            "SNI selector: bound host '{Host}' → cert {CertId} ({CertName}) for app '{AppName}' ({AppId})",
                            host,
                            certMeta.Id,
                            certMeta.Name,
                            app.Name,
                            app.Id);
                    }
                }
            }
        }

        List<X509Certificate2> oldLoaded;
        lock (_lock)
        {
            oldLoaded = _loaded;
            _loaded = loaded.Values.ToList();
            _byHost = byHost;
        }

        foreach (var cert in oldLoaded)
        {
            cert.Dispose();
        }

        _logger.LogInformation(
            "SNI selector: reload complete — {HostCount} host binding(s), {CertCount} unique cert(s) loaded",
            byHost.Count,
            loaded.Count);
    }

    private static X509Certificate2 LoadX509(CertificateMaterial material)
    {
        using var pem = X509Certificate2.CreateFromPem(material.CertificatePem, material.PrivateKeyPem);
        // Roundtrip via PFX so the private key is wired up correctly on Windows
        // (CreateFromPem alone leaves Schannel unable to find the key during the handshake).
        var pfx = pem.Export(X509ContentType.Pkcs12);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
    }

    public void Dispose()
    {
        _certSubscription?.Dispose();
        _appSubscription?.Dispose();
        lock (_lock)
        {
            foreach (var cert in _loaded)
            {
                cert.Dispose();
            }
            _loaded.Clear();
            _byHost.Clear();
        }
    }
}
