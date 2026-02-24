using CommerceHub.Api.Configuration;
using CommerceHub.Api.Interfaces;
using CommerceHub.Api.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CommerceHub.Api.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly IMongoCollection<Order> _collection;

    public OrderRepository(IMongoDatabase database, IOptions<MongoDbSettings> settings)
    {
        _collection = database.GetCollection<Order>(settings.Value.OrdersCollection);
    }

    public async Task<Order?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var filter = Builders<Order>.Filter.Eq(o => o.Id, id);
        return await _collection.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<Order> CreateAsync(Order order, CancellationToken ct = default)
    {
        await _collection.InsertOneAsync(order, cancellationToken: ct);
        return order;
    }

    public async Task<Order?> ReplaceAsync(string id, Order order, CancellationToken ct = default)
    {
        // Block replacement if the order is already Shipped — enforced atomically at the DB layer.
        // Using a compound filter prevents a TOCTOU race between checking status and replacing.
        var filter = Builders<Order>.Filter.And(
            Builders<Order>.Filter.Eq(o => o.Id, id),
            Builders<Order>.Filter.Ne(o => o.Status, OrderStatus.Shipped)
        );

        var options = new FindOneAndReplaceOptions<Order>
        {
            ReturnDocument = ReturnDocument.After
        };

        // Returns null if: document not found OR status == Shipped
        return await _collection.FindOneAndReplaceAsync(filter, order, options, ct);
    }
}
