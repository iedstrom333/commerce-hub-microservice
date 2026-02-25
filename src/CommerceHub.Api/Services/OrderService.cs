using CommerceHub.Api.Common;
using CommerceHub.Api.Configuration;
using CommerceHub.Api.DTOs;
using CommerceHub.Api.Events;
using CommerceHub.Api.Interfaces;
using CommerceHub.Api.Models;
using Microsoft.Extensions.Options;

namespace CommerceHub.Api.Services;

public class OrderService : IOrderService
{
    private static readonly Dictionary<string, HashSet<string>> ValidTransitions = new()
    {
        [OrderStatus.Pending]    = [OrderStatus.Processing, OrderStatus.Cancelled],
        [OrderStatus.Processing] = [OrderStatus.Shipped,    OrderStatus.Cancelled],
        [OrderStatus.Shipped]    = [],
        [OrderStatus.Cancelled]  = [],
    };

    private readonly IOrderRepository _orderRepo;
    private readonly IProductRepository _productRepo;
    private readonly IEventPublisher _publisher;
    private readonly IAuditRepository _auditRepo;
    private readonly IIdempotencyRepository _idempotencyRepo;
    private readonly RabbitMqSettings _mqSettings;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        IOrderRepository orderRepo,
        IProductRepository productRepo,
        IEventPublisher publisher,
        IOptions<RabbitMqSettings> mqSettings,
        IAuditRepository auditRepo,
        IIdempotencyRepository idempotencyRepo,
        ILogger<OrderService> logger)
    {
        _orderRepo        = orderRepo;
        _productRepo      = productRepo;
        _publisher        = publisher;
        _mqSettings       = mqSettings.Value;
        _auditRepo        = auditRepo;
        _idempotencyRepo  = idempotencyRepo;
        _logger           = logger;
    }

    public async Task<List<OrderResponseDto>> GetAllAsync(string? customerId = null, CancellationToken ct = default)
    {
        var orders = await _orderRepo.GetAllAsync(customerId, ct);
        return orders.Select(MapToDto).ToList();
    }

    public async Task<Result<OrderResponseDto>> CheckoutAsync(CheckoutRequestDto dto, string? idempotencyKey = null, CancellationToken ct = default)
    {
        // Idempotency: return the cached order if this key was already used.
        if (idempotencyKey is not null)
        {
            var existingOrderId = await _idempotencyRepo.GetOrderIdAsync(idempotencyKey, ct);
            if (existingOrderId is not null)
            {
                var cachedOrder = await _orderRepo.GetByIdAsync(existingOrderId, ct);
                if (cachedOrder is not null)
                    return Result<OrderResponseDto>.Ok(MapToDto(cachedOrder));
            }
        }

        // Service-layer validation: guard against negative or zero quantities.
        // DataAnnotations on the DTO also catch this at the controller boundary,
        // but we enforce it here defensively to keep the service self-contained.
        var invalidItem = dto.Items.FirstOrDefault(i => i.Quantity <= 0);
        if (invalidItem is not null)
            return Result<OrderResponseDto>.Fail($"Quantity must be at least 1 for product '{invalidItem.ProductId}'.");

        // Track which items were successfully decremented so we can roll back on failure.
        // StockAfter is the post-decrement stock returned by the atomic operation.
        var decremented = new List<(string ProductId, int Quantity, int StockAfter)>();

        try
        {
            var orderItems = new List<OrderItem>();
            decimal totalAmount = 0;

            foreach (var item in dto.Items)
            {
                var product = await _productRepo.DecrementStockAtomicAsync(item.ProductId, item.Quantity, ct);

                if (product is null)
                {
                    // Atomic decrement returned null — either product not found or insufficient stock.
                    // Roll back all previously decremented items before returning.
                    await RollbackStockAsync(decremented, ct);
                    return Result<OrderResponseDto>.Fail(
                        $"Insufficient stock or product not found: '{item.ProductId}'.");
                }

                decremented.Add((item.ProductId, item.Quantity, product.StockQuantity));
                orderItems.Add(new OrderItem
                {
                    ProductId = product.Id!,
                    ProductName = product.Name,
                    Quantity = item.Quantity,
                    UnitPrice = product.Price
                });
                totalAmount += product.Price * item.Quantity;
            }

            var order = new Order
            {
                CustomerId = dto.CustomerId,
                Items = orderItems,
                Status = OrderStatus.Pending,
                TotalAmount = totalAmount,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var created = await _orderRepo.CreateAsync(order, ct);

            // Persist the idempotency key so that retries with the same key return this order.
            if (idempotencyKey is not null)
                await _idempotencyRepo.StoreAsync(idempotencyKey, created.Id!, CancellationToken.None);

            // Write audit entries for each successfully decremented product now that we have the order ID.
            foreach (var (productId, qty, stockAfter) in decremented)
            {
                _ = _auditRepo.LogAsync(new AuditLog
                {
                    Timestamp = DateTime.UtcNow,
                    Event = "StockDecremented",
                    Actor = "Checkout",
                    EntityType = "Product",
                    EntityId = productId,
                    Delta = -qty,
                    StockBefore = stockAfter + qty,
                    StockAfter = stockAfter,
                    RelatedOrderId = created.Id
                }, CancellationToken.None);
            }

            // Event publication is best-effort. The order is already committed to MongoDB,
            // so we do NOT roll back stock if publishing fails. In production, an outbox
            // pattern would guarantee delivery.
            try
            {
                var evt = new OrderCreatedEvent
                {
                    OrderId = created.Id!,
                    CustomerId = created.CustomerId,
                    TotalAmount = created.TotalAmount,
                    CreatedAt = created.CreatedAt,
                    Items = created.Items.Select(i => new OrderCreatedEventItem
                    {
                        ProductId = i.ProductId,
                        Quantity = i.Quantity,
                        UnitPrice = i.UnitPrice
                    }).ToList()
                };
                await _publisher.PublishAsync(_mqSettings.OrderCreatedRoutingKey, evt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to publish OrderCreated event for order {OrderId}. Order is committed; manual retry required.",
                    created.Id);
            }

            return Result<OrderResponseDto>.Ok(MapToDto(created));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error during checkout. Rolling back stock for {Count} items.", decremented.Count);
            await RollbackStockAsync(decremented, ct);
            return Result<OrderResponseDto>.Fail("Checkout failed due to an internal error.");
        }
    }

    public async Task<OrderResponseDto?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var order = await _orderRepo.GetByIdAsync(id, ct);
        return order is null ? null : MapToDto(order);
    }

    public async Task<Result<OrderResponseDto>> UpdateAsync(string id, UpdateOrderDto dto, CancellationToken ct = default)
    {
        var existing = await _orderRepo.GetByIdAsync(id, ct);

        if (existing is null)
            return Result<OrderResponseDto>.Fail("NOT_FOUND");

        if (!ValidTransitions.TryGetValue(existing.Status, out var allowed) || !allowed.Contains(dto.Status))
            return Result<OrderResponseDto>.Fail(
                $"Cannot transition order from '{existing.Status}' to '{dto.Status}'.");

        var oldStatus = existing.Status.ToString();

        var updated = new Order
        {
            Id = id,
            CustomerId = dto.CustomerId,
            Items = dto.Items.Select(i => new OrderItem
            {
                ProductId = i.ProductId,
                ProductName = string.Empty,
                Quantity = i.Quantity,
                UnitPrice = 0
            }).ToList(),
            Status = dto.Status,
            TotalAmount = existing.TotalAmount,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };

        // ReplaceAsync also enforces the Shipped guard at the DB level via a compound filter,
        // providing an additional safety net against race conditions.
        var saved = await _orderRepo.ReplaceAsync(id, updated, ct);

        if (saved is null)
            return Result<OrderResponseDto>.Fail("NOT_FOUND");

        _ = _auditRepo.LogAsync(new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            Event = "OrderStatusChanged",
            Actor = "Fulfillment",
            EntityType = "Order",
            EntityId = id,
            OldStatus = oldStatus,
            NewStatus = dto.Status.ToString()
        }, CancellationToken.None);

        return Result<OrderResponseDto>.Ok(MapToDto(saved));
    }

    private async Task RollbackStockAsync(IEnumerable<(string ProductId, int Quantity, int StockAfter)> items, CancellationToken ct, string? orderId = null)
    {
        foreach (var (productId, qty, _) in items)
        {
            try
            {
                await _productRepo.IncrementStockAsync(productId, qty, ct);
                _logger.LogInformation("Rolled back {Quantity} units for product {ProductId}.", qty, productId);
                _ = _auditRepo.LogAsync(new AuditLog
                {
                    Timestamp = DateTime.UtcNow,
                    Event = "StockRolledBack",
                    Actor = "Checkout",
                    EntityType = "Product",
                    EntityId = productId,
                    Delta = qty,
                    RelatedOrderId = orderId
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "CRITICAL: Failed to roll back {Quantity} units for product {ProductId}. Manual correction required.",
                    qty, productId);
            }
        }
    }

    private static OrderResponseDto MapToDto(Order order) => new()
    {
        Id = order.Id!,
        CustomerId = order.CustomerId,
        Status = order.Status,
        TotalAmount = order.TotalAmount,
        CreatedAt = order.CreatedAt,
        UpdatedAt = order.UpdatedAt,
        Items = order.Items.Select(i => new OrderItemDto
        {
            ProductId = i.ProductId,
            ProductName = i.ProductName,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice
        }).ToList()
    };
}
