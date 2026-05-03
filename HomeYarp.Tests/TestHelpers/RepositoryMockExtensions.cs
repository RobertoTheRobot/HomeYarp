using HomeYarp.Application.Abstractions;

namespace HomeYarp.Tests.TestHelpers;

internal static class RepositoryMockExtensions
{
    /// <summary>
    /// Stubs both <see cref="IApplicationRepository.GetSnapshot"/> and <see cref="IApplicationRepository.GetAllAsync"/>
    /// from the same source so consumers that read either path observe the same data.
    /// </summary>
    public static IApplicationRepository WithApps(this IApplicationRepository repo, params DomainApplication[] apps)
    {
        repo.GetSnapshot().Returns(ApplicationSnapshot.FromItems(apps));
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(apps);
        return repo;
    }

    public static ICertificateRepository WithCerts(this ICertificateRepository repo, params Certificate[] certs)
    {
        repo.GetSnapshot().Returns(CertificateSnapshot.FromItems(certs));
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(certs);
        return repo;
    }
}
