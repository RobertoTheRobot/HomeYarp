using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeYarp.Application.Acme;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeYarp.Persistance.Json;

public sealed class FileAcmeAccountStore : IAcmeAccountStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly ILogger<FileAcmeAccountStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAcmeAccountStore(IOptions<JsonStoreOptions> options, ILogger<FileAcmeAccountStore>? logger = null)
    {
        var root = options.Value.DataRoot;
        if (!Path.IsPathRooted(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, root);
        }
        _directory = Path.Combine(root, "acme");
        Directory.CreateDirectory(_directory);
        _logger = logger ?? NullLogger<FileAcmeAccountStore>.Instance;
        _logger.LogInformation("FileAcmeAccountStore initialized at {Directory}", _directory);
    }

    public async Task<AcmeAccountRecord?> LoadAsync(string directoryUrl, CancellationToken cancellationToken = default)
    {
        var key = KeyFor(directoryUrl);
        var manifestPath = Path.Combine(_directory, $"account.{key}.json");
        var keyPath = Path.Combine(_directory, $"account.{key}.pem");
        if (!File.Exists(manifestPath) || !File.Exists(keyPath))
        {
            _logger.LogDebug("ACME account not found for directory {Directory} (key {Key})", directoryUrl, key);
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<AccountManifest>(stream, SerializerOptions, cancellationToken);
            if (manifest is null)
            {
                return null;
            }
            var pem = await File.ReadAllTextAsync(keyPath, cancellationToken);
            _logger.LogDebug("ACME account loaded for directory {Directory} (key {Key}, email {Email})", directoryUrl, key, manifest.Email);
            return new AcmeAccountRecord(
                manifest.DirectoryUrl,
                manifest.Email,
                pem,
                manifest.RegistrationLocation,
                manifest.AgreedAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AcmeAccountRecord record, CancellationToken cancellationToken = default)
    {
        var key = KeyFor(record.DirectoryUrl);
        var manifestPath = Path.Combine(_directory, $"account.{key}.json");
        var keyPath = Path.Combine(_directory, $"account.{key}.pem");
        var manifest = new AccountManifest
        {
            DirectoryUrl = record.DirectoryUrl,
            Email = record.Email,
            RegistrationLocation = record.RegistrationLocation,
            AgreedAt = record.AgreedAt
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAtomicAsync(keyPath, record.KeyPem, cancellationToken);
            var json = JsonSerializer.Serialize(manifest, SerializerOptions);
            await WriteAtomicAsync(manifestPath, json, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        _logger.LogInformation("ACME account saved for directory {Directory} (key {Key}, email {Email})", record.DirectoryUrl, key, record.Email);
    }

    private static string KeyFor(string directoryUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(directoryUrl));
        var hex = Convert.ToHexString(hash);
        return hex[..8].ToLowerInvariant();
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

    private sealed class AccountManifest
    {
        public string DirectoryUrl { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? RegistrationLocation { get; set; }
        public DateTimeOffset AgreedAt { get; set; }
    }
}
