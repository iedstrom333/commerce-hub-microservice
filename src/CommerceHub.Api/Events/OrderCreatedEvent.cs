namespace CommerceHub.Api.Events;

public class OrderCreatedEvent
{
    public required string OrderId { get; set; }
    public required string CustomerId { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAt { get; set; }
    public required List<OrderCreatedEventItem> Items { get; set; }
}

public class OrderCreatedEventItem
{
    public required string ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
