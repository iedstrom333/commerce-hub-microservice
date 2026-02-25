using CommerceHub.Api.Configuration;
using CommerceHub.Api.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CommerceHub.Api.Extensions;

public static class MongoIndexExtensions
{
    public static async Task EnsureIndexesAsync(this IMongoDatabase database, MongoDbSettings settings)
    {
        var auditLogs = database.GetCollection<AuditLog>(settings.AuditCollection);
        await auditLogs.Indexes.CreateManyAsync([
            // Primary audit query: all events for an entity, newest first
            new CreateIndexModel<AuditLog>(
                Builders<AuditLog>.IndexKeys
                    .Ascending(x => x.EntityId)
                    .Descending(x => x.Timestamp),
                new CreateIndexOptions { Name = "entityId_timestamp" }),
            // Look up all audit entries linked to a specific order
            new CreateIndexModel<AuditLog>(
                Builders<AuditLog>.IndexKeys.Ascending(x => x.RelatedOrderId),
                new CreateIndexOptions { Sparse = true, Name = "relatedOrderId" })
        ]);

        var orders = database.GetCollection<Order>(settings.OrdersCollection);
        await orders.Indexes.CreateOneAsync(
            new CreateIndexModel<Order>(
                Builders<Order>.IndexKeys.Ascending(x => x.CustomerId),
                new CreateIndexOptions { Name = "customerId" }));

        // Products: sku unique (data integrity) + stockQuantity (stock-guard filter performance)
        var products = database.GetCollection<Product>(settings.ProductsCollection);
        await products.Indexes.CreateManyAsync([
            new CreateIndexModel<Product>(
                Builders<Product>.IndexKeys.Ascending(x => x.Sku),
                new CreateIndexOptions { Unique = true, Name = "sku_unique" }),
            new CreateIndexModel<Product>(
                Builders<Product>.IndexKeys.Ascending(x => x.StockQuantity),
                new CreateIndexOptions { Name = "stockQuantity" })
        ]);

        // IdempotencyKeys: TTL index expires documents after 24 hours automatically.
        var idempotencyKeys = database.GetCollection<IdempotencyKey>(settings.IdempotencyKeysCollection);
        await idempotencyKeys.Indexes.CreateOneAsync(
            new CreateIndexModel<IdempotencyKey>(
                Builders<IdempotencyKey>.IndexKeys.Ascending(x => x.CreatedAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.FromHours(24), Name = "ttl_createdAt_24h" }));
    }
}
