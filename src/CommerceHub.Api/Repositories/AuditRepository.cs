using CommerceHub.Api.Configuration;
using CommerceHub.Api.Interfaces;
using CommerceHub.Api.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CommerceHub.Api.Repositories;

public class AuditRepository : IAuditRepository
{
    private readonly IMongoCollection<AuditLog> _collection;
    private readonly ILogger<AuditRepository> _logger;

    public AuditRepository(IMongoDatabase database, IOptions<MongoDbSettings> settings, ILogger<AuditRepository> logger)
    {
        _collection = database.GetCollection<AuditLog>(settings.Value.AuditCollection);
        _logger = logger;
    }

    public async Task LogAsync(AuditLog entry, CancellationToken ct = default)
    {
        try
        {
            await _collection.InsertOneAsync(entry, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write audit log entry for event {Event} on {EntityType} {EntityId}.",
                entry.Event, entry.EntityType, entry.EntityId);
        }
    }
}
