using HomeYarp.Application.Abstractions;

namespace HomeYarp.Persistance;

public sealed class HomeYarpDbContext
{
    public HomeYarpDbContext(IApplicationRepository applications, ICertificateRepository certificates)
    {
        Applications = applications;
        Certificates = certificates;
    }

    public IApplicationRepository Applications { get; }

    public ICertificateRepository Certificates { get; }
}
