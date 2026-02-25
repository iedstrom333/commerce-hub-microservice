using CommerceHub.Api.Interfaces;
using CommerceHub.Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;

namespace CommerceHub.Tests.Integration;

/// <summary>
/// Base class for all integration test fixtures.
/// Each fixture gets its own WebApplicationFactory pointed at the shared containers.
/// The database is wiped and re-seeded before every individual test.
/// </summary>
[Category("Integration")]
public abstract class IntegrationTestBase
{
    protected WebApplicationFactory<Program> Factory = null!;
    protected HttpClient Client = null!;
    protected IMongoDatabase Db  = null!;

    [OneTimeSetUp]
    public void CreateFactory()
    {
        var mongoCs      = GlobalTestSetup.Mongo.GetConnectionString();
        var rabbitHost   = GlobalTestSetup.Rabbit.Hostname;
        var rabbitPort   = GlobalTestSetup.Rabbit.GetMappedPublicPort(5672);

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MongoDbSettings:ConnectionString", mongoCs);
            builder.UseSetting("MongoDbSettings:DatabaseName",     "CommerceHubIntegration");
            builder.UseSetting("RabbitMqSettings:Host",     rabbitHost);
            builder.UseSetting("RabbitMqSettings:Port",     rabbitPort.ToString());
            builder.UseSetting("RabbitMqSettings:Username", "guest");
            builder.UseSetting("RabbitMqSettings:Password", "guest");
            // Replace the real RabbitMQ publisher with a no-op stub.
            // Integration tests verify the HTTP + MongoDB layer; the publisher's
            // fire-and-forget behaviour is covered by unit tests.
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IEventPublisher));
                if (existing != null) services.Remove(existing);
                services.AddSingleton<IEventPublisher, NoOpEventPublisher>();
            });
        });

        Client = Factory.CreateClient();

        var mongoClient = new MongoClient(mongoCs);
        Db = mongoClient.GetDatabase("CommerceHubIntegration");
    }

    [OneTimeTearDown]
    public void DisposeFactory()
    {
        Client.Dispose();
        Factory.Dispose();
    }

    /// <summary>
    /// Clears all runtime collections and re-inserts the three canonical seed products
    /// so every test starts from the same predictable state.
    /// </summary>
    [SetUp]
    public async Task ResetDatabase()
    {
        var empty = Builders<BsonDocument>.Filter.Empty;
        await Db.GetCollection<BsonDocument>("Products").DeleteManyAsync(empty);
        await Db.GetCollection<BsonDocument>("Orders").DeleteManyAsync(empty);
        await Db.GetCollection<BsonDocument>("AuditLogs").DeleteManyAsync(empty);
        await Db.GetCollection<BsonDocument>("IdempotencyKeys").DeleteManyAsync(empty);
        await SeedProductsAsync();
    }

    private async Task SeedProductsAsync()
    {
        await Db.GetCollection<Product>("Products").InsertManyAsync(
        [
            new Product { Id = "000000000000000000000001", Name = "Widget Pro",        Sku = "WGT-PRO-001", Price = 29.99m, StockQuantity = 100 },
            new Product { Id = "000000000000000000000002", Name = "Gadget Basic",      Sku = "GDG-BSC-002", Price = 14.50m, StockQuantity = 50  },
            new Product { Id = "000000000000000000000003", Name = "Thingamajig Elite", Sku = "TMJ-ELT-003", Price = 89.00m, StockQuantity = 5   },
        ]);
    }

    /// <summary>
    /// Polls the AuditLogs collection until a matching entry appears or the timeout expires.
    /// Needed because audit writes are fire-and-forget — they may not be committed
    /// at the moment the HTTP response is received.
    /// </summary>
    protected async Task<AuditLog?> WaitForAuditLogAsync(
        string eventName, string entityId, int timeoutMs = 3000)
    {
        var deadline   = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var collection = Db.GetCollection<AuditLog>("AuditLogs");
        var filter = Builders<AuditLog>.Filter.And(
            Builders<AuditLog>.Filter.Eq(x => x.Event,    eventName),
            Builders<AuditLog>.Filter.Eq(x => x.EntityId, entityId));

        while (DateTime.UtcNow < deadline)
        {
            var log = await collection.Find(filter).FirstOrDefaultAsync();
            if (log != null) return log;
            await Task.Delay(100);
        }

        return null;
    }

    /// <summary>
    /// Replaces the real RabbitMqEventPublisher so the integration test server
    /// starts without needing an AMQP connection. Publisher behaviour (fire-and-forget,
    /// resilience to failure) is already covered by the OrderService unit tests.
    /// </summary>
    private sealed class NoOpEventPublisher : IEventPublisher
    {
        public Task PublishAsync<T>(string routingKey, T payload, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
