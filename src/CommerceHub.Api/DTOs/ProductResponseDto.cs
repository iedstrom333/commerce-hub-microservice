namespace CommerceHub.Api.DTOs;

public class ProductResponseDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Sku { get; set; }
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
}
