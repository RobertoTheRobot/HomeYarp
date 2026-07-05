using HomeYarp.Application;
using HomeYarp.Application.Acme;
using HomeYarp.Persistance;
using HomeYarp.WebServer;
using HomeYarp.WebServer.Components;
using HomeYarp.WebServer.Mcp;
using ModelContextProtocol.Server;
using MudBlazor.Services;
using Serilog;
using Serilog.Events;

// Stage 1: bootstrap logger so we capture anything that explodes before the host is built.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting HomeYarp host");

    var builder = WebApplication.CreateBuilder(args);

    // Stage 2: real logger, fully driven from configuration. Sinks not declared in
    // appsettings.json's Serilog:WriteTo array are silent — packages just sit there.
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "HomeYarp"));

    builder.ConfigureHomeYarpKestrel();

    builder.Services.AddControllers();
    builder.Services.AddOpenApi();

    // Candidate policy backing RequireLocalPort — no-op on endpoints without the metadata.
    builder.Services.AddSingleton<MatcherPolicy, LocalPortMatcherPolicy>();

    builder.Services
        .AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddMudServices();

    builder.Services
        .AddHomeYarpPersistance(builder.Configuration)
        .AddHomeYarpApplication(builder.Configuration);

    builder.Services.AddReverseProxy();

    // MCP server: exposes the full management API as MCP tools over Streamable HTTP at /mcp.
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithTools<ApplicationTools>()
        .WithTools<CertificateTools>();

    var app = builder.Build();

    // One structured log line per HTTP request — method, path, status, elapsed.
    app.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = (httpContext, elapsed, ex) =>
        {
            if (ex is not null) return LogEventLevel.Error;
            if (httpContext.Response.StatusCode >= 500) return LogEventLevel.Error;
            if (httpContext.Response.StatusCode >= 400) return LogEventLevel.Warning;
            // Demote noisy framework polling (Blazor heartbeat, static asset traffic).
            var path = httpContext.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/_content", StringComparison.OrdinalIgnoreCase))
            {
                return LogEventLevel.Debug;
            }
            return LogEventLevel.Information;
        };
    });

    app.UseStaticFiles();
    app.UseAntiforgery();
    app.UseAuthorization();

    app.MapGet("/.well-known/acme-challenge/{token}", (string token, IAcmeChallengeStore store, ILogger<Program> logger) =>
    {
        if (store.TryGet(token, out var keyAuth))
        {
            logger.LogInformation("ACME HTTP-01 challenge served for token '{Token}'", token);
            return Results.Text(keyAuth, "text/plain");
        }
        logger.LogWarning("ACME HTTP-01 challenge requested for unknown token '{Token}'", token);
        return Results.NotFound();
    });

    // The management surface (UI + REST API + MCP + OpenAPI) and the YARP proxy share
    // listeners. Two independent gates scope it:
    //
    // 1. HomeYarp:Listeners:Management — THE security boundary. Management endpoints only
    //    route on connections that physically arrived on this port (Connection.LocalPort,
    //    which a client cannot spoof — unlike the Host header, which RequireHost matches:
    //    a WAN client hitting the public port 80 with `Host: x:5269` would sail through a
    //    RequireHost("*:5269") gate). Deployments keep this port off the WAN forward.
    //
    // 2. HomeYarp:Management:Hosts — optional cosmetic/legacy filter. Without it (or a
    //    management port), Razor's literal `/` outranks YARP's `/{**catch-all}` and
    //    HomeYarp's own pages are served on every proxied hostname. Both gates AND together.
    var managementPort = app.Configuration
        .GetSection(ListenerOptions.SectionName)
        .Get<ListenerOptions>()?.Management;
    var managementHosts = app.Configuration
        .GetSection("HomeYarp:Management:Hosts")
        .Get<string[]>() ?? [];

    var controllers = app.MapControllers();
    var razor = app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
    // MCP endpoint mirrors the REST API — same management restrictions apply.
    var mcp = app.MapMcp("/mcp");
    if (managementPort is int mgmtPort and > 0)
    {
        controllers.RequireLocalPort(mgmtPort);
        razor.RequireLocalPort(mgmtPort);
        mcp.RequireLocalPort(mgmtPort);
        Log.Information("Management surface (UI/API/MCP) restricted to connections on port {Port}", mgmtPort);
    }
    if (managementHosts.Length > 0)
    {
        controllers.RequireHost(managementHosts);
        razor.RequireHost(managementHosts);
        mcp.RequireHost(managementHosts);
        Log.Information("Management surface restricted to hosts: {Hosts}", managementHosts);
    }

    if (app.Environment.IsDevelopment())
    {
        var openApi = app.MapOpenApi();
        if (managementPort is int openApiPort and > 0) openApi.RequireLocalPort(openApiPort);
        if (managementHosts.Length > 0) openApi.RequireHost(managementHosts);
    }

    app.MapReverseProxy();

    Log.Information("HomeYarp host built; entering request loop");
    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "HomeYarp host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
