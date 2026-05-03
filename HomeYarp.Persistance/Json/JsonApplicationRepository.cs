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
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile ApplicationSnapshot _snapshot = ApplicationSnapshot.Empty;
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

        _snapshot = LoadFromDisk();
        _logger.LogInformation(
            "JsonApplicationRepository initialized at {Directory} with {Count} application(s)",
            _directory,
            _snapshot.All.Length);
    }

    public ApplicationSnapshot GetSnapshot() => _snapshot;

    public Task<IReadOnlyList<Domain.Application>> GetAllAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Domain.Application>>(_snapshot.All);

    public Task<Domain.Application?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_snapshot.ById.TryGetValue(id, out var app) ? app : null);

    public Task<Domain.Application?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(_snapshot.ByName.TryGetValue(name, out var app) ? app : null);

    public async Task AddAsync(Domain.Application application, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await WriteFileAsync(application, cancellationToken);
            _snapshot = ReplaceItem(_snapshot, application);
        }
        finally
        {
            _writeGate.Release();
        }
        _logger.LogDebug("Application '{AppName}' ({AppId}) persisted to disk (reload deferred)", application.Name, application.Id);
    }

    public async Task UpdateAsync(Domain.Application application, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await WriteFileAsync(application, cancellationToken);
            _snapshot = ReplaceItem(_snapshot, application);
        }
        finally
        {
            _writeGate.Release();
        }
        _logger.LogDebug("Application '{AppName}' ({AppId}) updated on disk (reload deferred)", application.Name, application.Id);
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
            var path = GetFilePath(id);
            if (File.Exists(path))
            {
                File.Delete(path);
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
            _logger.LogDebug("Application '{AppName}' ({AppId}) removed from disk (reload deferred)", removedName, id);
        }
        return removed;
    }

    public IChangeToken GetReloadToken() => new CancellationChangeToken(_reloadCts.Token);

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

    private ApplicationSnapshot LoadFromDisk()
    {
        var items = new List<Domain.Application>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var app = JsonSerializer.Deserialize<Domain.Application>(stream, SerializerOptions);
                if (app is not null)
                {
                    items.Add(app);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping malformed application file '{File}'", file);
            }
        }
        return ApplicationSnapshot.FromItems(items);
    }

    private static ApplicationSnapshot ReplaceItem(ApplicationSnapshot current, Domain.Application incoming)
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
        // Rebuild ById and ByName from newAll — robust against callers that mutate-in-place
        // (the same Application reference can carry a new Name, so a diff against `current` lies).
        return ApplicationSnapshot.FromItems(newAll);
    }

    private static ApplicationSnapshot RemoveItem(ApplicationSnapshot current, Guid id)
    {
        var newAll = current.All.RemoveAll(a => a.Id == id);
        return ApplicationSnapshot.FromItems(newAll);
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
}
