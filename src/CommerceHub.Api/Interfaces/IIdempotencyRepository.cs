namespace CommerceHub.Api.Interfaces;

public interface IIdempotencyRepository
{
    /// <summary>
    /// Returns the orderId that was stored for this key, or null if the key has never been used.
    /// </summary>
    Task<string?> GetOrderIdAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Stores a key → orderId mapping. No-op if the key already exists (duplicate request race).
    /// </summary>
    Task StoreAsync(string key, string orderId, CancellationToken ct = default);
}
