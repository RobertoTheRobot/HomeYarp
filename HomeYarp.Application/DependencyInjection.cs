using HomeYarp.Application.Acme;
using HomeYarp.Application.Proxy;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Application.Services;
using HomeYarp.Application.Tls;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Configuration;

namespace HomeYarp.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddHomeYarpApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AcmeOptions>(configuration.GetSection(AcmeOptions.SectionName));
        services.Configure<SelfSignedOptions>(configuration.GetSection(SelfSignedOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IApplicationService, ApplicationService>();
        services.AddScoped<ICertificateService, CertificateService>();
        services.AddScoped<IAcmeService, AcmeService>();
        services.AddScoped<ISelfSignedCertificateService, SelfSignedCertificateService>();

        services.AddSingleton<IProxyConfigProvider, HomeYarpConfigProvider>();
        services.AddSingleton<SniCertificateSelector>();
        services.AddSingleton<TlsPassthroughConnectionHandler>();
        services.AddSingleton<IAcmeChallengeStore, InMemoryAcmeChallengeStore>();

        services.AddHostedService<AcmeRenewalService>();
        services.AddHostedService<SelfSignedRenewalService>();

        return services;
    }
}
