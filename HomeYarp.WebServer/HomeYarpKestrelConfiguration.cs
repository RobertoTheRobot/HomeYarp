using HomeYarp.Application.Tls;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace HomeYarp.WebServer;

public static class HomeYarpKestrelConfiguration
{
    public static WebApplicationBuilder ConfigureHomeYarpKestrel(this WebApplicationBuilder builder)
    {
        var listeners = builder.Configuration.GetSection(ListenerOptions.SectionName).Get<ListenerOptions>() ?? new ListenerOptions();

        builder.WebHost.ConfigureKestrel(options =>
        {
            if (listeners.Http is int httpPort and > 0)
            {
                options.ListenAnyIP(httpPort);
            }

            if (listeners.HttpsOffload is int httpsPort and > 0)
            {
                options.ListenAnyIP(httpsPort, listenOptions =>
                {
                    listenOptions.UseHttps(httpsOptions =>
                    {
                        httpsOptions.ServerCertificateSelector = (_, hostName) =>
                        {
                            var selector = options.ApplicationServices.GetRequiredService<SniCertificateSelector>();
                            return selector.Select(hostName);
                        };
                    });
                });
            }

            if (listeners.HttpsPassthrough is int passthroughPort and > 0)
            {
                options.ListenAnyIP(passthroughPort, listenOptions =>
                {
                    listenOptions.UseConnectionHandler<TlsPassthroughConnectionHandler>();
                });
            }
        });

        return builder;
    }
}
