using Serilog;
using Blazorise;
using Blazorise.Bootstrap;
using Blazorise.Icons.FontAwesome;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Net;
using LettuceEncrypt;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.


builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile("/app/config/ReverseProxy.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile("/app/config/cert.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile("/app/config/auth.json", optional: true, reloadOnChange: true);


builder.Host
    .UseSerilog((hostistingContext, loggerConfiguration) => 
    loggerConfiguration
        .ReadFrom.Configuration(hostistingContext.Configuration)
    .Enrich.FromLogContext());


builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services
    .AddBlazorise(options =>
    {
        options.Immediate = true;
    })
    .AddBootstrapProviders()
    .AddFontAwesomeIcons();


builder.Services.AddControllers();

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddHttpForwarder();


if(builder.Environment.IsDevelopment())
{
    builder.WebHost.UseKestrel(k =>
    {
        k.Listen(IPAddress.Any,443);
    });
}
else
{
    // Lets encrypt and certificate only when deployed
    builder.Services
    .AddLettuceEncrypt()
    .PersistDataToDirectory(new DirectoryInfo("/app/config/data"), builder.Configuration["pfxPassword"]);

    builder.WebHost.UseKestrel(k =>
    {
        var appServices = k.ApplicationServices;
        k.Listen(
            IPAddress.Any, 443, listenOptions =>
                listenOptions.UseHttps(h =>
                {
                    h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    h.UseLettuceEncrypt(appServices);
                }));

    });
}





var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    //app.UseSwagger();
    //app.UseSwaggerUI();
}

//app.UseHttpsRedirection();

app.UseAuthorization();

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();

app.MapFallbackToPage("/_Host");

app.MapControllers();

app.MapReverseProxy();




app.Run();

