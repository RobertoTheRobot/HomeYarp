using HomeYarp.Application.Abstractions;
using HomeYarp.Persistance.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HomeYarp.Persistance;

public static class DependencyInjection
{
    public static IServiceCollection AddHomeYarpPersistance(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JsonStoreOptions>(configuration.GetSection(JsonStoreOptions.SectionName));
        services.AddSingleton<IApplicationRepository, JsonApplicationRepository>();
        services.AddSingleton<ICertificateRepository, JsonCertificateRepository>();
        services.AddScoped<HomeYarpDbContext>();
        return services;
    }
}
