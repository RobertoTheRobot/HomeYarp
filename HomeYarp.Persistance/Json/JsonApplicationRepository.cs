using System.Text.Json;
using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;
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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Domain.Application> _cache = new();
    private bool _loaded;
    private CancellationTokenSource _reloadCts = new();

    public JsonApplicationRepository(IOptions<JsonStoreOptions> options)
    {
        var root = options.Value.DataRoot;
        if (!Path.IsPathRooted(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, root);
        }
        _directory = Path.Combine(root, "applications");
        Directory.CreateDirectory(_directory);
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
        SignalReload();
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
        SignalReload();
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        bool removed;
        await _gate.WaitAsync(cancellationToken);
        try
        {
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
            SignalReload();
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
                catch (JsonException)
                {
                    // Skip malformed files; they'll be flagged by anyone tailing logs once we add logging.
                }
            }
            _loaded = true;
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

    private void SignalReload()
    {
        var oldCts = Interlocked.Exchange(ref _reloadCts, new CancellationTokenSource());
        try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
        oldCts.Dispose();
    }
}
