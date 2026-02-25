using CommerceHub.Api.Models;

namespace CommerceHub.Api.Interfaces;

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<List<Order>> GetAllAsync(string? customerId = null, CancellationToken ct = default);
    Task<Order> CreateAsync(Order order, CancellationToken ct = default);
    Task<Order?> ReplaceAsync(string id, Order order, CancellationToken ct = default);
}
