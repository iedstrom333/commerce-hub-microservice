using CommerceHub.Api.Configuration;
using CommerceHub.Api.Extensions;
using CommerceHub.Api.HealthChecks;
using CommerceHub.Api.Middleware;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration sections
builder.Services.Configure<MongoDbSettings>(
    builder.Configuration.GetSection("MongoDbSettings"));
builder.Services.Configure<RabbitMqSettings>(
    builder.Configuration.GetSection("RabbitMqSettings"));

// Register infrastructure and application services
builder.Services.AddMongoDB();
builder.Services.AddRabbitMq();
builder.Services.AddApplicationServices();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("mongodb");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Commerce Hub API", Version = "v1" });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db       = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();
    var settings = scope.ServiceProvider.GetRequiredService<IOptions<MongoDbSettings>>().Value;
    await db.EnsureIndexesAsync(settings);
}

app.UseExceptionHandler();
app.MapHealthChecks("/health");

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Commerce Hub API v1"));

app.MapControllers();

app.Run();

// Make Program partial for integration test WebApplicationFactory usage
public partial class Program { }
