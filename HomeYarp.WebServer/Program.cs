using HomeYarp.Application;
using HomeYarp.Application.Acme;
using HomeYarp.Persistance;
using HomeYarp.WebServer;
using HomeYarp.WebServer.Components;
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

    builder.Services
        .AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services
        .AddHomeYarpPersistance(builder.Configuration)
        .AddHomeYarpApplication(builder.Configuration);

    builder.Services.AddReverseProxy();

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

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

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

    app.MapControllers();

    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

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
