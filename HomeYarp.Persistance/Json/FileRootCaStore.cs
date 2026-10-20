using HomeYarp.Application.RootCa;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeYarp.Persistance.Json;

/// <summary>
/// Persists the root CA as <c>{DataRoot}/ca/root.cert.pem</c> + <c>root.key.pem</c> (atomic temp-file rename,
/// key file 0600 on Unix). The CA key is the most sensitive file HomeYarp owns: anyone holding it can mint a
/// trusted certificate for any hostname on every device that trusts the root.
/// </summary>
public sealed class FileRootCaStore : IRootCaStore
{
    private readonly string _directory;
    private readonly ILogger<FileRootCaStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileRootCaStore(IOptions<JsonStoreOptions> options, ILogger<FileRootCaStore>? logger = null)
    {
        var root = options.Value.DataRoot;
        if (!Path.IsPathRooted(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, root);
        }
        _directory = Path.Combine(root, "ca");
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        _logger = logger ?? NullLogger<FileRootCaStore>.Instance;
        _logger.LogInformation("FileRootCaStore initialized at {Directory}", _directory);
    }

    private string CertPath => Path.Combine(_directory, "root.cert.pem");
    private string KeyPath => Path.Combine(_directory, "root.key.pem");

    public async Task<RootCaMaterial?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(CertPath) || !File.Exists(KeyPath))
            {
                return null;
            }
            return new RootCaMaterial(
                await File.ReadAllTextAsync(CertPath, cancellationToken),
                await File.ReadAllTextAsync(KeyPath, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(RootCaMaterial material, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAtomicAsync(KeyPath, material.PrivateKeyPem, secret: true, cancellationToken);
            await WriteAtomicAsync(CertPath, material.CertificatePem, secret: false, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        _logger.LogInformation("Root CA saved to {Directory}", _directory);
    }

    private static async Task WriteAtomicAsync(string finalPath, string contents, bool secret, CancellationToken cancellationToken)
    {
        var tempPath = finalPath + ".tmp";
        if (secret && !OperatingSystem.IsWindows())
        {
            // Create with 0600 from the start so the key is never world-readable, even briefly.
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            };
            await using var stream = new FileStream(tempPath, options);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(contents.AsMemory(), cancellationToken);
        }
        else
        {
            await File.WriteAllTextAsync(tempPath, contents, cancellationToken);
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
}
