using MongoDB.Bson.Serialization.Attributes;

namespace CommerceHub.Api.Models;

public class IdempotencyKey
{
    // The key itself is the document _id — gives a free unique index.
    [BsonId]
    public required string Key { get; set; }

    [BsonElement("orderId")]
    public string? OrderId { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }
}
