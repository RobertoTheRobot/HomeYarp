using Microsoft.Extensions.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.


builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile("/app/config/ReverseProxy.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile("/app/config/auth.json", optional: true, reloadOnChange: true);


builder.Host
    .UseSerilog((hostistingContext, loggerConfiguration) => 
    loggerConfiguration
        .ReadFrom.Configuration(hostistingContext.Configuration)
    .Enrich.FromLogContext());


builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));


builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
//builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen();

builder.WebHost.UseKestrel();
builder.WebHost
    .ConfigureKestrel((hostingConfiguration, serverOptions) =>
    {
        serverOptions.ListenAnyIP(5555);
    });


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    //app.UseSwagger();
    //app.UseSwaggerUI();
}

//app.UseHttpsRedirection();

app.UseAuthorization();


app.MapControllers();
app.MapReverseProxy();

app.Run();
