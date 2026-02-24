using CommerceHub.Api.DTOs;
using CommerceHub.Api.Models;

namespace CommerceHub.Tests.Helpers;

public static class TestDataBuilder
{
    public const string ProductId1 = "000000000000000000000001";
    public const string ProductId2 = "000000000000000000000002";
    public const string OrderId1   = "aaaaaaaaaaaaaaaaaaaaaaaa";

    public static Product BuildProduct(
        string id             = ProductId1,
        int    stockQuantity  = 100,
        decimal price         = 29.99m,
        string name           = "Widget Pro",
        string sku            = "WGT-PRO-001") => new()
    {
        Id            = id,
        Name          = name,
        Sku           = sku,
        Price         = price,
        StockQuantity = stockQuantity
    };

    public static CheckoutRequestDto BuildCheckoutRequest(
        string productId = ProductId1,
        int    quantity  = 2,
        string customerId = "CUST-001") => new()
    {
        CustomerId = customerId,
        Items      = [new CheckoutItemDto { ProductId = productId, Quantity = quantity }]
    };

    public static Order BuildOrder(
        string id         = OrderId1,
        string status     = OrderStatus.Pending,
        string customerId = "CUST-001") => new()
    {
        Id         = id,
        CustomerId = customerId,
        Status     = status,
        Items      = [],
        TotalAmount = 0,
        CreatedAt  = DateTime.UtcNow,
        UpdatedAt  = DateTime.UtcNow
    };
}
