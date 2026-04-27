using HomeYarp.Application;
using HomeYarp.Application.Acme;
using HomeYarp.Persistance;
using HomeYarp.WebServer;
using HomeYarp.WebServer.Components;

var builder = WebApplication.CreateBuilder(args);

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthorization();

app.MapGet("/.well-known/acme-challenge/{token}", (string token, IAcmeChallengeStore store) =>
    store.TryGet(token, out var keyAuth)
        ? Results.Text(keyAuth, "text/plain")
        : Results.NotFound());

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapReverseProxy();

app.Run();
