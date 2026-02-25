using CommerceHub.Api.Common;
using CommerceHub.Api.DTOs;

namespace CommerceHub.Api.Interfaces;

public interface IOrderService
{
    Task<Result<OrderResponseDto>> CheckoutAsync(CheckoutRequestDto dto, string? idempotencyKey = null, CancellationToken ct = default);
    Task<OrderResponseDto?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<List<OrderResponseDto>> GetAllAsync(string? customerId = null, CancellationToken ct = default);
    Task<Result<OrderResponseDto>> UpdateAsync(string id, UpdateOrderDto dto, CancellationToken ct = default);
}
