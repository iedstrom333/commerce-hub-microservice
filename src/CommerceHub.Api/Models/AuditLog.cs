using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CommerceHub.Api.Models;

public class AuditLog
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("timestamp")]  public DateTime Timestamp { get; set; }
    [BsonElement("event")]      public required string Event { get; set; }
    [BsonElement("actor")]      public required string Actor { get; set; }
    [BsonElement("entityType")] public required string EntityType { get; set; }
    [BsonElement("entityId")]   public required string EntityId { get; set; }

    // Stock change fields (null for order events)
    [BsonElement("delta")]          public int? Delta { get; set; }
    [BsonElement("stockBefore")]    public int? StockBefore { get; set; }
    [BsonElement("stockAfter")]     public int? StockAfter { get; set; }
    [BsonElement("relatedOrderId")] public string? RelatedOrderId { get; set; }

    // Order status change fields (null for stock events)
    [BsonElement("oldStatus")] public string? OldStatus { get; set; }
    [BsonElement("newStatus")] public string? NewStatus { get; set; }
}
