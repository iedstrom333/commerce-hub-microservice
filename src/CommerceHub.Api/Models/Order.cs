using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CommerceHub.Api.Models;

public class Order
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("customerId")]
    public required string CustomerId { get; set; }

    [BsonElement("items")]
    public required List<OrderItem> Items { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = OrderStatus.Pending;

    [BsonElement("totalAmount")]
    public decimal TotalAmount { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class OrderStatus
{
    public const string Pending    = "Pending";
    public const string Processing = "Processing";
    public const string Shipped    = "Shipped";
    public const string Cancelled  = "Cancelled";
}
