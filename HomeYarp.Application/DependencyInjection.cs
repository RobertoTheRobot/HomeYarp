using HomeYarp.Application.Proxy;
using HomeYarp.Application.Services;
using HomeYarp.Application.Tls;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;

namespace HomeYarp.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddHomeYarpApplication(this IServiceCollection services)
    {
        services.AddScoped<IApplicationService, ApplicationService>();
        services.AddScoped<ICertificateService, CertificateService>();
        services.AddSingleton<IProxyConfigProvider, HomeYarpConfigProvider>();
        services.AddSingleton<SniCertificateSelector>();
        services.AddSingleton<TlsPassthroughConnectionHandler>();
        return services;
    }
}
