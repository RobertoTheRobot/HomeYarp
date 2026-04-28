using System.Text.Json;
using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace HomeYarp.Persistance.Json;

public sealed class JsonCertificateRepository : ICertificateRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly ILogger<JsonCertificateRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Certificate> _cache = new();
    private bool _loaded;
    private CancellationTokenSource _reloadCts = new();

    public JsonCertificateRepository(IOptions<JsonStoreOptions> options, ILogger<JsonCertificateRepository>? logger = null)
    {
        var root = options.Value.DataRoot;
        if (!Path.IsPathRooted(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, root);
        }
        _directory = Path.Combine(root, "certificates");
        Directory.CreateDirectory(_directory);
        _logger = logger ?? NullLogger<JsonCertificateRepository>.Instance;
        _logger.LogInformation("JsonCertificateRepository initialized at {Directory}", _directory);
    }

    public async Task<IReadOnlyList<Certificate>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _cache.Values.ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Certificate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _cache.TryGetValue(id, out var c) ? c : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Certificate?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _cache.Values.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CertificateMaterial?> GetMaterialAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var certPath = GetCertPath(id);
        var keyPath = GetKeyPath(id);
        if (!File.Exists(certPath) || !File.Exists(keyPath))
        {
            return null;
        }
        var cert = await File.ReadAllTextAsync(certPath, cancellationToken);
        var key = await File.ReadAllTextAsync(keyPath, cancellationToken);
        return new CertificateMaterial(cert, key);
    }

    public async Task SaveAsync(Certificate certificate, CertificateMaterial material, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cache[certificate.Id] = certificate;
            await WriteAtomicAsync(GetCertPath(certificate.Id), material.CertificatePem, cancellationToken);
            await WriteAtomicAsync(GetKeyPath(certificate.Id), material.PrivateKeyPem, cancellationToken);
            await WriteManifestAsync(certificate, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        _logger.LogDebug("Certificate '{CertName}' ({CertId}) persisted to disk (reload deferred)", certificate.Name, certificate.Id);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        bool removed;
        string? removedName = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(id, out var existing))
            {
                removedName = existing.Name;
            }
            removed = _cache.Remove(id);
            if (removed)
            {
                foreach (var path in new[] { GetManifestPath(id), GetCertPath(id), GetKeyPath(id) })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        if (removed)
        {
            _logger.LogDebug("Certificate '{CertName}' ({CertId}) removed from disk (reload deferred)", removedName, id);
        }
        return removed;
    }

    public IChangeToken GetReloadToken() => new CancellationChangeToken(_reloadCts.Token);

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    await using var stream = File.OpenRead(file);
                    var cert = await JsonSerializer.DeserializeAsync<Certificate>(stream, SerializerOptions, cancellationToken);
                    if (cert is not null)
                    {
                        _cache[cert.Id] = cert;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Skipping malformed certificate manifest '{File}'", file);
                }
            }
            _loaded = true;
            _logger.LogInformation("Loaded {Count} certificate(s) from {Directory}", _cache.Count, _directory);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteManifestAsync(Certificate certificate, CancellationToken cancellationToken)
    {
        var finalPath = GetManifestPath(certificate.Id);
        var tempPath = finalPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, certificate, SerializerOptions, cancellationToken);
        }
        if (File.Exists(finalPath))
        {
            File.Replace(tempPath, finalPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, finalPath);
        }
    }

    private static async Task WriteAtomicAsync(string finalPath, string contents, CancellationToken cancellationToken)
    {
        var tempPath = finalPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, contents, cancellationToken);
        if (File.Exists(finalPath))
        {
            File.Replace(tempPath, finalPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, finalPath);
        }
    }

    private string GetManifestPath(Guid id) => Path.Combine(_directory, id.ToString("N") + ".json");

    private string GetCertPath(Guid id) => Path.Combine(_directory, id.ToString("N") + ".cert.pem");

    private string GetKeyPath(Guid id) => Path.Combine(_directory, id.ToString("N") + ".key.pem");

    public void SignalReload()
    {
        var oldCts = Interlocked.Exchange(ref _reloadCts, new CancellationTokenSource());
        try { oldCts.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _logger.LogError(ex, "Certificate reload callbacks threw"); }
        oldCts.Dispose();
        _logger.LogInformation("JsonCertificateRepository reload signal fired");
    }
}
