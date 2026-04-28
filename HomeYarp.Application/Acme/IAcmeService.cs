using HomeYarp.Domain;

namespace HomeYarp.Application.Acme;

public interface IAcmeService
{
    Task<Certificate> IssueAsync(
        string name,
        string? friendlyName,
        IReadOnlyList<string> hostnames,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null);

    Task<Certificate> RenewAsync(Guid certificateId, CancellationToken cancellationToken = default);
}
