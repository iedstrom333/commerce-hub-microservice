namespace CommerceHub.Api.DTOs;

public class ProductStockResponseDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public int StockQuantity { get; set; }
}
