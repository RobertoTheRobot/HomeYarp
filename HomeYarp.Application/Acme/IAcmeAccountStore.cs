namespace HomeYarp.Application.Acme;

public interface IAcmeAccountStore
{
    Task<AcmeAccountRecord?> LoadAsync(string directoryUrl, CancellationToken cancellationToken = default);

    Task SaveAsync(AcmeAccountRecord record, CancellationToken cancellationToken = default);
}

public sealed record AcmeAccountRecord(
    string DirectoryUrl,
    string Email,
    string KeyPem,
    string? RegistrationLocation,
    DateTimeOffset AgreedAt);
