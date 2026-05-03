using System.Security.Cryptography.X509Certificates;
using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace HomeYarp.Application.Tls;

public sealed class SniCertificateSelector : IDisposable
{
    /// <summary>
    /// Delay before disposing replaced/orphaned X509Certificate2 instances so Schannel can finish
    /// in-flight handshakes that captured the prior reference. Disposing under it produces
    /// "Cannot find the requested object" errors on the very next handshake.
    /// </summary>
    private static readonly TimeSpan DeferredDisposeDelay = TimeSpan.FromSeconds(60);

    private readonly ICertificateRepository _certificates;
    private readonly IApplicationRepository _applications;
    private readonly ILogger<SniCertificateSelector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _reloadLock = new();
    private readonly CancellationTokenSource _disposeCts = new();

    // Lock-free per-handshake reads: a single volatile load returns the current binding map.
    // Writers in ReloadCore build a fresh Dictionary, populate it fully, then atomically swap
    // the reference. Volatile release-on-write / acquire-on-read guarantees readers never see
    // a half-constructed dict. The dict itself is treated as immutable post-publication.
    private volatile Dictionary<string, X509Certificate2> _byHost = new(StringComparer.OrdinalIgnoreCase);
    // Persistent across reloads — reuse loaded X509 instances when the cert metadata's thumbprint
    // hasn't changed. Touched ONLY inside _reloadLock (ReloadCore + Dispose).
    private Dictionary<Guid, LoadedCert> _loadedCerts = new();
    private IDisposable? _certSubscription;
    private IDisposable? _appSubscription;

    public SniCertificateSelector(
        ICertificateRepository certificates,
        IApplicationRepository applications,
        ILogger<SniCertificateSelector>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _certificates = certificates;
        _applications = applications;
        _logger = logger ?? NullLogger<SniCertificateSelector>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Reload();
        _certSubscription = ChangeToken.OnChange(_certificates.GetReloadToken, Reload);
        _appSubscription = ChangeToken.OnChange(_applications.GetReloadToken, Reload);
    }

    public X509Certificate2? Select(string? sni)
    {
        // Single volatile read — no lock per TLS handshake.
        var snapshot = _byHost;

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
        try
        {
            ReloadCore();
        }
        catch (Exception ex)
        {
            // Swallow so the change-token re-registration in ChangeToken.OnChange still runs.
            // Cert bindings stay on the previous snapshot — restart re-reads from disk.
            _logger.LogError(ex, "SNI selector reload failed; cert bindings NOT updated. Restart to recover.");
        }
    }

    private void ReloadCore()
    {
        lock (_reloadLock)
        {
            _logger.LogDebug("SNI selector: reload triggered (apps or certs changed)");

            var apps = _applications.GetSnapshot().All;
            var certById = _certificates.GetSnapshot().ById;

            var newLoaded = new Dictionary<Guid, LoadedCert>();
            var newByHost = new Dictionary<string, X509Certificate2>(StringComparer.OrdinalIgnoreCase);
            var reused = 0;
            var fresh = 0;

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

                if (!newLoaded.TryGetValue(certMeta.Id, out var entry))
                {
                    if (_loadedCerts.TryGetValue(certMeta.Id, out var cached)
                        && string.Equals(cached.Thumbprint, certMeta.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        // Cache hit — same cert id and same thumbprint. Skip the disk read + PEM parse.
                        entry = cached;
                        reused++;
                        _logger.LogDebug(
                            "SNI selector: reused cached cert {CertId} ({CertName}) thumbprint={Thumbprint}",
                            certMeta.Id,
                            certMeta.Name,
                            cached.Thumbprint);
                    }
                    else
                    {
                        var loaded = TryLoadFresh(certMeta, app);
                        if (loaded is null)
                        {
                            continue;
                        }
                        entry = loaded;
                        fresh++;

                        // Replacing a previously-cached version of the same cert id — defer-dispose
                        // the prior instance so Schannel finishes in-flight handshakes against it.
                        if (cached is not null)
                        {
                            ScheduleDeferredDispose(cached.Cert, $"replaced cert {certMeta.Id}");
                        }
                    }

                    newLoaded[certMeta.Id] = entry;
                }

                foreach (var route in app.Routes)
                {
                    foreach (var host in route.Hosts)
                    {
                        if (!string.IsNullOrWhiteSpace(host))
                        {
                            newByHost[host] = entry.Cert;
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

            // Identify orphans — cert ids in the previous cache that no app references anymore.
            // Defer-dispose so any in-flight Schannel handshake completes.
            var orphaned = 0;
            foreach (var (id, cached) in _loadedCerts)
            {
                if (!newLoaded.ContainsKey(id))
                {
                    ScheduleDeferredDispose(cached.Cert, $"orphaned cert {id}");
                    orphaned++;
                }
            }

            // _loadedCerts is only touched under _reloadLock, no extra protection needed.
            // _byHost is volatile — atomic reference swap publishes the new dict to lock-free readers.
            _loadedCerts = newLoaded;
            _byHost = newByHost;

            _logger.LogInformation(
                "SNI selector: reload complete — {HostCount} host binding(s), {CertCount} unique cert(s) ({Reused} reused, {Fresh} loaded from disk, {Orphaned} orphan(s) scheduled for disposal)",
                newByHost.Count,
                newLoaded.Count,
                reused,
                fresh,
                orphaned);
        }
    }

    private LoadedCert? TryLoadFresh(Certificate certMeta, Domain.Application app)
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
            return null;
        }

        try
        {
            var x509 = LoadX509(material);
            _logger.LogDebug(
                "SNI selector: loaded cert {CertId} ({CertName}) thumbprint={Thumbprint} notAfter={NotAfter:o}",
                certMeta.Id,
                certMeta.Name,
                x509.Thumbprint,
                x509.NotAfter);
            return new LoadedCert(x509, x509.Thumbprint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SNI selector: failed to load cert {CertId} ({CertName}) for app '{AppName}' ({AppId})",
                certMeta.Id,
                certMeta.Name,
                app.Name,
                app.Id);
            return null;
        }
    }

    private void ScheduleDeferredDispose(X509Certificate2 cert, string reason)
    {
        var ct = _disposeCts.Token;
        _ = Task.Delay(DeferredDisposeDelay, _timeProvider, ct).ContinueWith(t =>
        {
            if (t.IsCanceled)
            {
                return;
            }
            try
            {
                cert.Dispose();
                _logger.LogDebug("SNI selector: disposed deferred cert ({Reason})", reason);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SNI selector: deferred dispose failed ({Reason})", reason);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
        // Cancel pending deferred-dispose tasks before tearing down the cache.
        try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }
        lock (_reloadLock)
        {
            foreach (var entry in _loadedCerts.Values)
            {
                try { entry.Cert.Dispose(); } catch { /* best-effort on shutdown */ }
            }
            _loadedCerts.Clear();
            // Replace _byHost with an empty dict (don't Clear the published one — a concurrent
            // Select on another thread may still be reading it).
            _byHost = new Dictionary<string, X509Certificate2>(StringComparer.OrdinalIgnoreCase);
        }
        _disposeCts.Dispose();
    }

    private sealed record LoadedCert(X509Certificate2 Cert, string Thumbprint);
}
