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
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile CertificateSnapshot _snapshot = CertificateSnapshot.Empty;
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

        _snapshot = LoadFromDisk();
        _logger.LogInformation(
            "JsonCertificateRepository initialized at {Directory} with {Count} certificate(s)",
            _directory,
            _snapshot.All.Length);
    }

    public CertificateSnapshot GetSnapshot() => _snapshot;

    public Task<IReadOnlyList<Certificate>> GetAllAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Certificate>>(_snapshot.All);

    public Task<Certificate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_snapshot.ById.TryGetValue(id, out var c) ? c : null);

    public Task<Certificate?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(_snapshot.ByName.TryGetValue(name, out var c) ? c : null);

    public async Task<CertificateMaterial?> GetMaterialAsync(Guid id, CancellationToken cancellationToken = default)
    {
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
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await WriteAtomicAsync(GetCertPath(certificate.Id), material.CertificatePem, cancellationToken);
            await WriteAtomicAsync(GetKeyPath(certificate.Id), material.PrivateKeyPem, cancellationToken);
            await WriteManifestAsync(certificate, cancellationToken);
            _snapshot = ReplaceItem(_snapshot, certificate);
        }
        finally
        {
            _writeGate.Release();
        }
        _logger.LogDebug("Certificate '{CertName}' ({CertId}) persisted to disk (reload deferred)", certificate.Name, certificate.Id);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        bool removed;
        string? removedName = null;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (!_snapshot.ById.TryGetValue(id, out var existing))
            {
                return false;
            }
            removedName = existing.Name;
            foreach (var path in new[] { GetManifestPath(id), GetCertPath(id), GetKeyPath(id) })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            _snapshot = RemoveItem(_snapshot, id);
            removed = true;
        }
        finally
        {
            _writeGate.Release();
        }
        if (removed)
        {
            _logger.LogDebug("Certificate '{CertName}' ({CertId}) removed from disk (reload deferred)", removedName, id);
        }
        return removed;
    }

    public IChangeToken GetReloadToken() => new CancellationChangeToken(_reloadCts.Token);

    public void SignalReload()
    {
        var oldCts = Interlocked.Exchange(ref _reloadCts, new CancellationTokenSource());
        try { oldCts.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _logger.LogError(ex, "Certificate reload callbacks threw"); }
        oldCts.Dispose();
        _logger.LogInformation("JsonCertificateRepository reload signal fired");
    }

    private CertificateSnapshot LoadFromDisk()
    {
        var items = new List<Certificate>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var cert = JsonSerializer.Deserialize<Certificate>(stream, SerializerOptions);
                if (cert is not null)
                {
                    items.Add(cert);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping malformed certificate manifest '{File}'", file);
            }
        }
        return CertificateSnapshot.FromItems(items);
    }

    private static CertificateSnapshot ReplaceItem(CertificateSnapshot current, Certificate incoming)
    {
        var all = current.All;
        var existingIndex = -1;
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i].Id == incoming.Id)
            {
                existingIndex = i;
                break;
            }
        }

        var newAll = existingIndex >= 0 ? all.SetItem(existingIndex, incoming) : all.Add(incoming);
        return CertificateSnapshot.FromItems(newAll);
    }

    private static CertificateSnapshot RemoveItem(CertificateSnapshot current, Guid id)
    {
        var newAll = current.All.RemoveAll(c => c.Id == id);
        return CertificateSnapshot.FromItems(newAll);
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
}
