var builder = WebApplication.CreateBuilder(args);

// Add services to the container.


builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile("/app/config/ReverseProxy.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("/app/config/AppSettings.json", optional: true, reloadOnChange: true);

builder.Services.AddReverseProxy();


builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
