using CommerceHub.Api.Common;
using CommerceHub.Api.DTOs;
using CommerceHub.Api.Interfaces;
using CommerceHub.Api.Models;

namespace CommerceHub.Api.Services;

public class ProductService : IProductService
{
    private readonly IProductRepository _productRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly ILogger<ProductService> _logger;

    public ProductService(IProductRepository productRepo, IAuditRepository auditRepo, ILogger<ProductService> logger)
    {
        _productRepo = productRepo;
        _auditRepo = auditRepo;
        _logger = logger;
    }

    public async Task<List<ProductResponseDto>> GetAllAsync(CancellationToken ct = default)
    {
        var products = await _productRepo.GetAllAsync(ct);
        return products.Select(p => new ProductResponseDto
        {
            Id            = p.Id!,
            Name          = p.Name,
            Sku           = p.Sku,
            Price         = p.Price,
            StockQuantity = p.StockQuantity
        }).ToList();
    }

    public async Task<Result<ProductStockResponseDto>> AdjustStockAsync(
        string productId, int delta, CancellationToken ct = default)
    {
        if (delta == 0)
            return Result<ProductStockResponseDto>.Fail("Delta cannot be zero.");

        var product = await _productRepo.AdjustStockAtomicAsync(productId, delta, ct);

        if (product is null)
        {
            // The repository returns null either when the product is not found
            // or when the adjustment would cause negative stock.
            // We perform a secondary read to distinguish the two cases
            // so we can return an accurate error message.
            var exists = await _productRepo.GetByIdAsync(productId, ct);
            if (exists is null)
                return Result<ProductStockResponseDto>.Fail("NOT_FOUND");

            return Result<ProductStockResponseDto>.Fail(
                $"Adjustment of {delta} units would cause negative stock. Current stock: {exists.StockQuantity}.");
        }

        _logger.LogInformation(
            "Stock adjusted by {Delta} for product {ProductId}. New quantity: {Quantity}.",
            delta, productId, product.StockQuantity);

        _ = _auditRepo.LogAsync(new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            Event = "StockAdjusted",
            Actor = "Warehouse",
            EntityType = "Product",
            EntityId = productId,
            Delta = delta,
            StockBefore = product.StockQuantity - delta,
            StockAfter = product.StockQuantity
        }, CancellationToken.None);

        return Result<ProductStockResponseDto>.Ok(new ProductStockResponseDto
        {
            Id = product.Id!,
            Name = product.Name,
            StockQuantity = product.StockQuantity
        });
    }
}
