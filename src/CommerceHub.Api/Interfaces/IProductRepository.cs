using CommerceHub.Api.Models;

namespace CommerceHub.Api.Interfaces;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Atomically decrements stock if sufficient quantity is available.
    /// Returns null if product not found or stock is insufficient.
    /// </summary>
    Task<Product?> DecrementStockAtomicAsync(string productId, int quantity, CancellationToken ct = default);

    /// <summary>
    /// Atomically adjusts stock by delta. For negative deltas, prevents stock from going below 0.
    /// Returns null if product not found or the adjustment would cause negative stock.
    /// </summary>
    Task<Product?> AdjustStockAtomicAsync(string productId, int delta, CancellationToken ct = default);

    /// <summary>
    /// Increments stock unconditionally. Used for compensating rollback during checkout failure.
    /// </summary>
    Task IncrementStockAsync(string productId, int quantity, CancellationToken ct = default);
}
