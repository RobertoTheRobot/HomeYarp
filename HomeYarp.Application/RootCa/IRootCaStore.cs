namespace HomeYarp.Application.RootCa;

public interface IRootCaStore
{
    Task<RootCaMaterial?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(RootCaMaterial material, CancellationToken cancellationToken = default);
}

public sealed record RootCaMaterial(string CertificatePem, string PrivateKeyPem);
