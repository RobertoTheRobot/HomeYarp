using System.Text.Json;
using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace HomeYarp.Persistance.Json;

public sealed class JsonApplicationRepository : IApplicationRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly ILogger<JsonApplicationRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Domain.Application> _cache = new();
    private bool _loaded;
    private CancellationTokenSource _reloadCts = new();

    public JsonApplicationRepository(IOptions<JsonStoreOptions> options, ILogger<JsonApplicationRepository>? logger = null)
    {
        var root = options.Value.DataRoot;
        if (!Path.IsPathRooted(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, root);
        }
        _directory = Path.Combine(root, "applications");
        Directory.CreateDirectory(_directory);
        _logger = logger ?? NullLogger<JsonApplicationRepository>.Instance;
        _logger.LogInformation("JsonApplicationRepository initialized at {Directory}", _directory);
    }

    public async Task<IReadOnlyList<Domain.Application>> GetAllAsync(CancellationToken cancellationToken = default)
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

    public async Task<Domain.Application?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _cache.TryGetValue(id, out var app) ? app : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Domain.Application?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _cache.Values.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(Domain.Application application, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cache[application.Id] = application;
            await WriteFileAsync(application, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        _logger.LogDebug("Application '{AppName}' ({AppId}) persisted to disk (reload deferred)", application.Name, application.Id);
    }

    public async Task UpdateAsync(Domain.Application application, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cache[application.Id] = application;
            await WriteFileAsync(application, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        _logger.LogDebug("Application '{AppName}' ({AppId}) updated on disk (reload deferred)", application.Name, application.Id);
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
                var path = GetFilePath(id);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        if (removed)
        {
            _logger.LogDebug("Application '{AppName}' ({AppId}) removed from disk (reload deferred)", removedName, id);
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
                    var app = await JsonSerializer.DeserializeAsync<Domain.Application>(stream, SerializerOptions, cancellationToken);
                    if (app is not null)
                    {
                        _cache[app.Id] = app;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Skipping malformed application file '{File}'", file);
                }
            }
            _loaded = true;
            _logger.LogInformation("Loaded {Count} application(s) from {Directory}", _cache.Count, _directory);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteFileAsync(Domain.Application application, CancellationToken cancellationToken)
    {
        var finalPath = GetFilePath(application.Id);
        var tempPath = finalPath + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, application, SerializerOptions, cancellationToken);
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

    private string GetFilePath(Guid id) => Path.Combine(_directory, id.ToString("N") + ".json");

    public void SignalReload()
    {
        var oldCts = Interlocked.Exchange(ref _reloadCts, new CancellationTokenSource());
        // Cancel runs registered ChangeToken callbacks synchronously on this thread.
        // Each consumer (HomeYarpConfigProvider, SniCertificateSelector) catches its
        // own exceptions, so a bad cert can't propagate back through the request that
        // triggered the reload and leave the in-memory snapshot half-swapped.
        try { oldCts.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _logger.LogError(ex, "Application reload callbacks threw"); }
        oldCts.Dispose();
        _logger.LogInformation("JsonApplicationRepository reload signal fired");
    }
}
