using CommerceHub.Api.Common;
using CommerceHub.Api.DTOs;

namespace CommerceHub.Api.Interfaces;

public interface IProductService
{
    Task<List<ProductResponseDto>> GetAllAsync(CancellationToken ct = default);
    Task<Result<ProductStockResponseDto>> AdjustStockAsync(string productId, int delta, CancellationToken ct = default);
}
