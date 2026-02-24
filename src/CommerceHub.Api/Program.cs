using CommerceHub.Api.Configuration;
using CommerceHub.Api.Extensions;

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

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Commerce Hub API", Version = "v1" });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Commerce Hub API v1"));

app.MapControllers();

app.Run();

// Make Program partial for integration test WebApplicationFactory usage
public partial class Program { }
