using CommerceHub.Api.Configuration;
using CommerceHub.Api.Interfaces;
using CommerceHub.Api.Messaging;
using CommerceHub.Api.Repositories;
using CommerceHub.Api.Services;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CommerceHub.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMongoDB(this IServiceCollection services)
    {
        services.AddSingleton<IMongoClient>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<MongoDbSettings>>().Value;
            return new MongoClient(settings.ConnectionString);
        });

        services.AddSingleton<IMongoDatabase>(sp =>
        {
            var client = sp.GetRequiredService<IMongoClient>();
            var settings = sp.GetRequiredService<IOptions<MongoDbSettings>>().Value;
            return client.GetDatabase(settings.DatabaseName);
        });

        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();

        return services;
    }

    public static IServiceCollection AddRabbitMq(this IServiceCollection services)
    {
        // The publisher is a singleton — creating connections per-request is expensive.
        // We use an async factory and block synchronously during app startup,
        // which is acceptable for one-time initialization.
        services.AddSingleton<IEventPublisher>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<RabbitMqSettings>>().Value;
            var logger = sp.GetRequiredService<ILogger<RabbitMqEventPublisher>>();

            return RabbitMqEventPublisher.CreateAsync(settings, logger)
                .GetAwaiter()
                .GetResult();
        });

        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IProductService, ProductService>();

        return services;
    }
}
