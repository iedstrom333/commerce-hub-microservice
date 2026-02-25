# Developer Log — Commerce Hub Microservice

This log documents the AI-augmented development process for the Commerce Hub assignment, detailing AI strategy, human corrections, and test generation.

---

## AI Strategy

### Context Injection Approach

Rather than asking the AI to generate boilerplate code, I front-loaded it with the full architectural schema before any code was written. The initial prompt included:

- **Data schemas** (Order, Product documents with field types and MongoDB BSON attributes)
- **Atomicity constraints** (MongoDB single-document guarantee, compensating transaction requirement for standalone instance)
- **Interface contracts** (all four endpoint behaviors, including idempotency semantics for PUT)
- **Technology constraints** (.NET 8, MongoDB.Driver 3.x, RabbitMQ.Client 7.x with async API)

This upfront context allowed the AI to generate architecturally correct code on the first pass rather than producing generic templates that needed heavy refactoring.

### Layered Code Generation

Code was generated in dependency order: models → interfaces → repositories → services → controllers. This prevented circular dependency issues and let each layer be validated before the next was built.

### Test Generation Strategy

Unit tests were generated after specifying exact scenarios rather than asking the AI to "write tests." Each scenario was stated with:
1. The observable behavior (what should happen)
2. The verification target (which mock interaction to assert)
3. The edge case reason (why this specific scenario matters)

This produced tests that assert intent, not implementation details.

---

## Human Audit: Four Specific Corrections

### Correction 1: RabbitMQ Singleton Registration Pattern

**AI's initial output:** The AI suggested registering `RabbitMqEventPublisher` as a scoped service and creating a new connection per request:

```csharp
// AI's initial suggestion (incorrect)
services.AddScoped<IEventPublisher, RabbitMqEventPublisher>();
```

**Problem identified:** Creating a new TCP connection to RabbitMQ for every HTTP request would cause connection pool exhaustion under load and dramatically slow response times. RabbitMQ connections are expensive (~100ms to establish) and meant to be long-lived.

**Human correction:** Changed to a singleton registered via async factory that blocks synchronously only once at startup:

```csharp
// Corrected implementation
services.AddSingleton<IEventPublisher>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<RabbitMqSettings>>().Value;
    var logger = sp.GetRequiredService<ILogger<RabbitMqEventPublisher>>();
    return RabbitMqEventPublisher.CreateAsync(settings, logger)
        .GetAwaiter().GetResult();
});
```

The async factory pattern was required because `IChannel.CreateChannelAsync()` in RabbitMQ.Client v7 is asynchronous and cannot be called in a constructor. The `GetAwaiter().GetResult()` blocking call is acceptable here because it occurs exactly once during application startup, not during request handling.

---

### Correction 2: Checkout Rollback Not Implemented

**AI's initial output:** The AI generated a checkout flow that checked all stock levels first (read-only), then decremented:

```csharp
// AI's initial suggestion (TOCTOU race condition)
foreach (var item in dto.Items)
{
    var product = await _productRepo.GetByIdAsync(item.ProductId, ct);
    if (product.StockQuantity < item.Quantity)
        return Result.Fail("Insufficient stock");
}
// Then decrement each item (not atomic — another request could win between check and update)
foreach (var item in dto.Items)
    await _productRepo.DecrementStockAsync(item.ProductId, item.Quantity, ct);
```

**Problem identified:** This is a classic check-then-act race condition (TOCTOU — Time of Check, Time of Use). Between the stock check and the decrement, another concurrent request could successfully claim the same inventory, causing stock to go negative.

**Human correction:** Two changes were enforced:

1. Replaced the two-step read+write with a single atomic `FindOneAndUpdateAsync` using a compound filter:

```csharp
// Filter combines existence check AND stock guard in one atomic DB round-trip
var filter = Filter.And(
    Filter.Eq(p => p.Id, productId),
    Filter.Gte(p => p.StockQuantity, quantity)  // Gte = "greater than or equal"
);
var update = Update.Inc(p => p.StockQuantity, -quantity);
return await _collection.FindOneAndUpdateAsync(filter, update, options, ct);
```

2. Added compensating rollback: if any item in the checkout fails after others have been decremented, all decremented items are re-incremented via `IncrementStockAsync`.

---

### Correction 3: PUT Shipped Guard — Service vs. Repository Enforcement

**AI's initial output:** The Shipped guard was only checked in `OrderService.UpdateAsync` before calling the repository:

```csharp
// AI's initial suggestion — guard only in service layer
if (existing.Status == OrderStatus.Shipped)
    return Result.Fail("Cannot modify a shipped order.");
await _orderRepo.ReplaceAsync(id, updated, ct);
```

**Problem identified:** This creates a TOCTOU race condition. Between the service reading the order status and calling `ReplaceAsync`, another concurrent request could change the order status to Shipped. The guard in the service layer would pass, but the second request would incorrectly replace a Shipped order.

**Human correction:** The guard was pushed into the repository using a compound MongoDB filter, making the check and the replacement an atomic operation:

```csharp
// Repository-level atomic guard — prevents race condition
var filter = Filter.And(
    Filter.Eq(o => o.Id, id),
    Filter.Ne(o => o.Status, OrderStatus.Shipped)  // Ne = "not equal"
);
return await _collection.FindOneAndReplaceAsync(filter, order, options, ct);
// Returns null if: not found OR status == Shipped
```

The service layer check was kept as well — it provides a fast-path failure response with a meaningful error message before making a DB round-trip. The repository-level guard is the actual concurrency safety mechanism.

---

### Correction 4: CancellationToken Bug in Fire-and-Forget Audit Writes

**Discovery:** After the audit log system was implemented, I inspected the MongoDB `AuditLogs` collection directly and found it completely empty despite placing orders and adjusting stock. No errors were surfaced in the API responses.

**Root cause identified:** The fire-and-forget audit writes used the HTTP request's `CancellationToken`:

```csharp
// Buggy — ct is the HTTP request's CancellationToken
_ = _auditRepo.LogAsync(entry, ct);
```

ASP.NET Core cancels this token as soon as the HTTP response is sent. Since the fire-and-forget task had not yet started, `InsertOneAsync` received an already-cancelled token, threw `OperationCanceledException`, and the silent try/catch in `LogAsync` discarded it. The collection remained empty.

**Human correction:** Changed all four fire-and-forget `LogAsync` calls to `CancellationToken.None`:

```csharp
// Fixed — audit write lifecycle is independent of the HTTP request
_ = _auditRepo.LogAsync(entry, CancellationToken.None);
```

This was non-obvious because the bug produced no errors — only silent data loss. Discovering it required manually querying the database after placing a test order.

---

## Verification: AI-Assisted Test Generation for Edge Cases

AI was prompted with specific edge case scenarios rather than asked to "generate comprehensive tests":

### Edge Case: Mid-Checkout Stock Failure Rollback

Prompt given to AI:
> "Generate a test where the first of two checkout items decrements successfully, but the second item returns null (insufficient stock). Assert that: (a) the result is a failure, (b) IncrementStockAsync is called once for the first item with the exact quantity, (c) CreateAsync is never called on the order repository."

This produced `CheckoutAsync_WhenSecondItemOutOfStock_RollsBackFirstItemAndReturnsFailure` which validates the compensating transaction behavior that is easy to overlook.

### Edge Case: Publisher Failure Does Not Rollback

During review, I identified that the AI's first draft would have rolled back stock if the RabbitMQ publish failed. This is incorrect behavior — the order is already committed to MongoDB, so rolling back stock would create a phantom order with no corresponding inventory decrement.

The AI was prompted:
> "The event publish is best-effort. If `PublishAsync` throws, log the error and return success — the order is committed. Do NOT roll back stock."

This produced the correct try/catch structure around the publish call that logs but does not propagate the exception.

### Edge Case: Zero Delta for Stock Adjustment

The `PATCH /api/products/{id}/stock` endpoint accepts a `delta` field. A zero delta is semantically meaningless and could mask client bugs. The AI was asked to add early rejection before the DB call, resulting in the `AdjustStockAsync_WhenDeltaIsZero_ReturnsFailureWithoutCallingRepo` test that verifies no DB round-trip occurs for this invalid input.

---

## Additional Features Directed by Human

The following features were built by the AI at explicit human direction.

### System Diagrams

**Requested:** Generate all architectural and process diagrams for the system.

**AI produced:**
- **Sequence diagrams** — end-to-end request flows for checkout, stock adjustment, and order status update, showing actor → controller → service → repository → MongoDB/RabbitMQ interactions at each step
- **UML class diagram** — full system structure showing all models, interfaces, repositories, services, and controllers with their relationships and cardinalities
- **ERD** — MongoDB collection schemas for `Orders`, `Products`, `AuditLogs`, and `IdempotencyKeys` with field types and index annotations
- **Use-case diagrams** — four actor perspectives (Customer, Warehouse Manager, Fulfillment Staff, Downstream Service) mapping each actor to the endpoints they interact with

---

### RabbitMQ Queue Pre-Configuration

**Requested:** Add configuration so the `order.created` queue and binding exist before any message is published, enabling the RabbitMQ Management UI to show queued messages for inspection.

**AI produced:** A `rabbitmq-definitions.json` file mounted into the RabbitMQ container via `docker-compose.yml`. The file pre-declares:
- A `topic` exchange named `commerce_hub`
- A durable queue named `order.created`
- A binding from the exchange to the queue using routing key `order.created`

**Why it was needed:** Without the queue declared in advance, RabbitMQ would discard any message published to the exchange before a consumer connected and declared the queue. Pre-configuring via definitions ensures messages are retained and visible in the Management UI at `http://localhost:15672` immediately after checkout, without requiring a running consumer.

---

### Integration Tests

**Requested:** Generate integration tests using Testcontainers to spin up real MongoDB and RabbitMQ instances in Docker, testing the full stack from HTTP request to database state.

**AI produced:** An integration test suite (`tests/CommerceHub.Tests/Integration/`) covering:
- Full checkout happy path: POST → 201, verify order in MongoDB, verify stock decremented
- Idempotency: same `Idempotency-Key` header on two POST requests returns the same order ID both times
- Insufficient stock: POST with quantity exceeding stock → 422, verify no order created, verify stock unchanged
- Stock adjustment happy path: PATCH → 200, verify new stock level in MongoDB
- Order status update: PUT → 200, verify status change in MongoDB
- Shipped guard: PUT on a Shipped order → 409 conflict

Tests use `WebApplicationFactory<Program>` with Testcontainers-managed containers, ensuring no mocking of infrastructure — every assertion is against real MongoDB documents.

---

### Manual Testing Guide

**Requested:** Generate a comprehensive manual testing guide with exact curl commands covering all happy paths and edge cases for every endpoint.

**AI produced:** `manualTesting.txt` covering all four system actors:
- Customer: happy path order, zero/negative quantity rejection, insufficient stock, mid-checkout rollback verification
- Warehouse: restock, manual decrement, zero delta, negative-beyond-stock, product not found
- Fulfillment: state machine transitions (Pending → Processing → Shipped), terminal-state lock (409)
- Downstream: RabbitMQ Management UI inspection of queued `OrderCreatedEvent` payloads

The guide also documents the reset procedure (`docker-compose down -v && docker-compose up --build`) needed to restore seed stock levels between test sessions.

---

### Performance and Security Hardening

**Requested:** Identify and implement quick performance and security improvements. Four items were selected from a ranked list and implemented:

**1. Swagger gated to Development environment**

`app.UseSwagger()` and `app.UseSwaggerUI()` were moved inside `if (app.Environment.IsDevelopment())`. Previously, Swagger was unconditionally registered, exposing the full API schema and a browsable UI in production. Gating it to Development eliminates that surface without any runtime cost.

**2. `[MaxLength]` DataAnnotations on all DTO string fields**

`[MaxLength(50)]` was added to `CustomerId` in `CheckoutRequestDto` and `UpdateOrderDto`. `[MaxLength(24, ErrorMessage = "ProductId must be a 24-character ObjectId.")]` was added to `ProductId` in `CheckoutItemDto`. Without these, the model binder would accept arbitrarily long strings before any business logic ran — a low-effort input-size abuse vector. The 24-character limit on `ProductId` also documents the MongoDB ObjectId format constraint directly in the DTO.

**3. Response compression**

`AddResponseCompression(opts => opts.EnableForHttps = true)` was registered in the service collection and `app.UseResponseCompression()` was added to the middleware pipeline. JSON payloads (product lists, order bodies) compress well; this reduces bandwidth for list responses with no application-layer changes.

**4. Products indexes declared at startup**

`sku_unique` (unique index) and `stockQuantity` (ascending index) for the `Products` collection were added to `EnsureIndexesAsync` in `MongoIndexExtensions.cs`. Previously these indexes were only created by the seed script, meaning a fresh container with a different seed path would run the stock-guard `FindOneAndUpdateAsync` filter against an unindexed `stockQuantity` field — a full collection scan on every checkout item. Declaring them at startup guarantees they exist regardless of how the database was populated.

---

## Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Checkout rollback strategy | Compensating transactions | MongoDB replica set (required for ACID transactions) not available in standalone docker-compose |
| Result type | Custom `Result<T>` record | No external library dependency; keeps service return types explicit |
| Event publish failure | Log, don't rollback | Order is committed; stock rollback would create inconsistency |
| Shipped guard placement | Both service and repository | Service: fast response; Repository: atomic race-condition safety |
| RabbitMQ exchange type | `topic` | Allows future consumers to subscribe to patterns (e.g., `order.*`) |
| Message delivery mode | `Persistent` | Messages survive broker restart |
