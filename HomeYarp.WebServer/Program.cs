using HomeYarp.Application;
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
    .AddHomeYarpApplication();

builder.Services.AddReverseProxy();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthorization();

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapReverseProxy();

app.Run();
