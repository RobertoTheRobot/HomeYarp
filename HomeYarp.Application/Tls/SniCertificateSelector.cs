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
            _logger.LogDebug("SNI selector: empty SNI, no cert returned");
            return null;
        }

        _logger.LogDebug("SNI selector: sni='{Sni}', cache hosts=[{Hosts}]", sni, string.Join(",", snapshot.Keys));

        if (snapshot.TryGetValue(sni, out var exact))
        {
            return exact;
        }

        var dot = sni.IndexOf('.');
        if (dot > 0)
        {
            var wildcard = "*." + sni[(dot + 1)..];
            if (snapshot.TryGetValue(wildcard, out var wild))
            {
                return wild;
            }
        }

        return null;
    }

    private void Reload()
    {
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
            if (!app.Enabled || app.Tls.Mode != TlsMode.Offload || app.Tls.CertificateId is null)
            {
                continue;
            }

            if (!certById.TryGetValue(app.Tls.CertificateId.Value, out var certMeta))
            {
                continue;
            }

            if (!loaded.TryGetValue(certMeta.Id, out var loadedCert))
            {
                var material = _certificates.GetMaterialAsync(certMeta.Id).GetAwaiter().GetResult();
                if (material is null)
                {
                    continue;
                }

                try
                {
                    loadedCert = LoadX509(material);
                    loaded[certMeta.Id] = loadedCert;
                }
                catch
                {
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
