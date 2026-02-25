using CommerceHub.Api.Configuration;
using CommerceHub.Api.Interfaces;
using CommerceHub.Api.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CommerceHub.Api.Repositories;

public class IdempotencyRepository : IIdempotencyRepository
{
    private readonly IMongoCollection<IdempotencyKey> _collection;
    private readonly ILogger<IdempotencyRepository> _logger;

    public IdempotencyRepository(
        IMongoDatabase database,
        IOptions<MongoDbSettings> settings,
        ILogger<IdempotencyRepository> logger)
    {
        _collection = database.GetCollection<IdempotencyKey>(settings.Value.IdempotencyKeysCollection);
        _logger = logger;
    }

    public async Task<string?> GetOrderIdAsync(string key, CancellationToken ct = default)
    {
        var doc = await _collection.Find(x => x.Key == key).FirstOrDefaultAsync(ct);
        return doc?.OrderId;
    }

    public async Task StoreAsync(string key, string orderId, CancellationToken ct = default)
    {
        try
        {
            await _collection.InsertOneAsync(
                new IdempotencyKey { Key = key, OrderId = orderId, CreatedAt = DateTime.UtcNow },
                cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Code == 11000)
        {
            // Duplicate key — a concurrent request with the same key already stored an order.
            // Safe to ignore: the first writer wins.
            _logger.LogWarning(
                "Idempotency key {Key} already stored by a concurrent request.", key);
        }
    }
}
