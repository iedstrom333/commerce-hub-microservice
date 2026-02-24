using CommerceHub.Api.Configuration;
using CommerceHub.Api.Interfaces;
using CommerceHub.Api.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CommerceHub.Api.Repositories;

public class ProductRepository : IProductRepository
{
    private readonly IMongoCollection<Product> _collection;

    public ProductRepository(IMongoDatabase database, IOptions<MongoDbSettings> settings)
    {
        _collection = database.GetCollection<Product>(settings.Value.ProductsCollection);
    }

    public async Task<Product?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var filter = Builders<Product>.Filter.Eq(p => p.Id, id);
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<Product?> DecrementStockAtomicAsync(string productId, int quantity, CancellationToken ct = default)
    {
        // This single FindOneAndUpdateAsync call is the race-condition guard.
        //
        // The Gte filter ensures we only decrement if sufficient stock exists.
        // Two concurrent requests for the last 3 units will serialize at MongoDB —
        // only one will match the filter and decrement; the other returns null.
        var filter = Builders<Product>.Filter.And(
            Builders<Product>.Filter.Eq(p => p.Id, productId),
            Builders<Product>.Filter.Gte(p => p.StockQuantity, quantity)
        );

        var update = Builders<Product>.Update.Inc(p => p.StockQuantity, -quantity);

        var options = new FindOneAndUpdateOptions<Product>
        {
            ReturnDocument = ReturnDocument.After
        };

        return await _collection.FindOneAndUpdateAsync(filter, update, options, ct);
    }

    public async Task<Product?> AdjustStockAtomicAsync(string productId, int delta, CancellationToken ct = default)
    {
        FilterDefinition<Product> filter;

        if (delta < 0)
        {
            // For negative deltas, guard against going below zero.
            // The absolute value of a negative delta is -delta.
            filter = Builders<Product>.Filter.And(
                Builders<Product>.Filter.Eq(p => p.Id, productId),
                Builders<Product>.Filter.Gte(p => p.StockQuantity, -delta)
            );
        }
        else
        {
            filter = Builders<Product>.Filter.Eq(p => p.Id, productId);
        }

        var update = Builders<Product>.Update.Inc(p => p.StockQuantity, delta);

        var options = new FindOneAndUpdateOptions<Product>
        {
            ReturnDocument = ReturnDocument.After
        };

        return await _collection.FindOneAndUpdateAsync(filter, update, options, ct);
    }

    public async Task IncrementStockAsync(string productId, int quantity, CancellationToken ct = default)
    {
        var filter = Builders<Product>.Filter.Eq(p => p.Id, productId);
        var update = Builders<Product>.Update.Inc(p => p.StockQuantity, quantity);
        await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}
