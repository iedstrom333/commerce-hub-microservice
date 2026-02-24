namespace CommerceHub.Api.DTOs;

public class OrderResponseDto
{
    public required string Id { get; set; }
    public required string CustomerId { get; set; }
    public required List<OrderItemDto> Items { get; set; }
    public required string Status { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class OrderItemDto
{
    public required string ProductId { get; set; }
    public required string ProductName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
