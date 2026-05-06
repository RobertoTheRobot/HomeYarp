using HomeYarp.Application.Tls;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HomeYarp.WebServer;

public static class HomeYarpKestrelConfiguration
{
    public static WebApplicationBuilder ConfigureHomeYarpKestrel(this WebApplicationBuilder builder)
    {
        var listeners = builder.Configuration.GetSection(ListenerOptions.SectionName).Get<ListenerOptions>() ?? new ListenerOptions();

        Log.Information(
            "Kestrel listener config: Http={Http} HttpsOffload={HttpsOffload} HttpsPassthrough={HttpsPassthrough}",
            listeners.Http,
            listeners.HttpsOffload,
            listeners.HttpsPassthrough);

        if (listeners.Http is null or 0
            && listeners.HttpsOffload is null or 0
            && listeners.HttpsPassthrough is null or 0)
        {
            Log.Warning("All HomeYarp listeners are disabled — no inbound traffic will be accepted");
        }

        builder.WebHost.ConfigureKestrel(options =>
        {
            // HomeYarp is a reverse proxy — gating proxied request bodies at Kestrel's 30 MB
            // default produces spurious 413s on legitimate flows like Docker registry layer
            // pushes. Backends decide their own limits. Override via Kestrel:Limits:MaxRequestBodySize
            // in config if a cap is wanted.
            options.Limits.MaxRequestBodySize = null;

            if (listeners.Http is int httpPort and > 0)
            {
                options.ListenAnyIP(httpPort);
                Log.Information("Listening for HTTP on port {Port}", httpPort);
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
                Log.Information("Listening for HTTPS-offload on port {Port} (SNI selection from cert store)", httpsPort);
            }

            if (listeners.HttpsPassthrough is int passthroughPort and > 0)
            {
                options.ListenAnyIP(passthroughPort, listenOptions =>
                {
                    listenOptions.UseConnectionHandler<TlsPassthroughConnectionHandler>();
                });
                Log.Information("Listening for HTTPS-passthrough (raw L4 + SNI peek) on port {Port}", passthroughPort);
            }
        });

        return builder;
    }
}
