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

## Human Audit: Corrections and Refinements

### Correction 1: Swagger Exposed in All Environments

**Problem identified:** After reviewing `Program.cs`, I noticed Swagger was registered unconditionally — it would serve the full API schema and an interactive UI in any environment, including production. This is an unnecessary attack surface.

**Directed fix:** Wrapped the Swagger middleware in an environment guard. The AI implemented:

```csharp
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Commerce Hub API v1"));
}
```

---

### Correction 2: DTO String Fields Had No Length Limits

**Problem identified:** Reviewing the DTOs, `CustomerId` and `ProductId` accepted strings of unlimited length. Without bounds, the model binder would pass arbitrarily long input all the way through to MongoDB before any rejection could occur.

**Directed fix:** Added `[MaxLength]` annotations. The AI implemented `[MaxLength(50)]` on `CustomerId` in both checkout and update DTOs, and `[MaxLength(24)]` on `ProductId` with an error message documenting the MongoDB ObjectId format constraint.

---

### Correction 3: Products Indexes Not Declared at Startup

**Problem identified:** Inspecting `MongoIndexExtensions.cs`, I found that the Products collection had no indexes registered at startup. The `sku` uniqueness constraint and the `stockQuantity` index used by the stock-guard filter were only created by the seed script — a fragile dependency.

**Directed fix:** Added both indexes to `EnsureIndexesAsync` so they exist regardless of how the database was populated:

```csharp
var products = database.GetCollection<Product>(settings.ProductsCollection);
await products.Indexes.CreateManyAsync([
    new CreateIndexModel<Product>(
        Builders<Product>.IndexKeys.Ascending(x => x.Sku),
        new CreateIndexOptions { Unique = true, Name = "sku_unique" }),
    new CreateIndexModel<Product>(
        Builders<Product>.IndexKeys.Ascending(x => x.StockQuantity),
        new CreateIndexOptions { Name = "stockQuantity" })
]);
```

---

### Correction 4: CancellationToken Bug in Fire-and-Forget Audit Writes

**Discovery:** After the audit log system was implemented, I inspected the MongoDB `AuditLogs` collection directly and found it completely empty despite placing orders and adjusting stock. No errors were surfaced in the API responses.

**Root cause identified:** The fire-and-forget audit writes used the HTTP request's `CancellationToken`:

```csharp
// Buggy — ct is the HTTP request's CancellationToken
_ = _auditRepo.LogAsync(entry, ct);
```

ASP.NET Core cancels this token as soon as the HTTP response is sent. Since the fire-and-forget task had not yet started, `InsertOneAsync` received an already-cancelled token, threw `OperationCanceledException`, and the silent try/catch in `LogAsync` discarded it. The collection remained empty.

**Directed fix:** Changed all four fire-and-forget `LogAsync` calls to `CancellationToken.None`:

```csharp
// Fixed — audit write lifecycle is independent of the HTTP request
_ = _auditRepo.LogAsync(entry, CancellationToken.None);
```

This was non-obvious because the bug produced no errors — only silent data loss. Discovering it required manually querying the database after placing a test order.

---

## Verification: AI-Assisted Test Generation for Edge Cases

Tests were generated by specifying exact scenarios rather than asking the AI to "write tests." Each scenario was given with the observable behavior to verify, the specific mock interaction to assert, and the reason the edge case matters.

### Edge Case: Mid-Checkout Stock Failure Rollback

I directed the AI to generate a test for a two-item checkout where the first item decrements successfully but the second fails (insufficient stock). The assertions targeted: the result is a failure, rollback is called for the first item with the exact quantity, and the order repository's `CreateAsync` is never invoked. This validated the compensating transaction behavior that is easy to overlook — the test is `CheckoutAsync_WhenSecondItemOutOfStock_RollsBackFirstItemAndReturnsFailure`.

### Edge Case: Publisher Failure Does Not Rollback

I directed a test to verify that a RabbitMQ publish failure after a successful checkout does not roll back the stock or return an error. The order is already committed to MongoDB; rolling back stock would create a phantom order with no inventory decrement. The test is `CheckoutAsync_WhenPublishFails_ReturnsSuccessAndDoesNotRollback`.

### Edge Case: Zero Delta for Stock Adjustment

I directed a test for the `PATCH /api/products/{id}/stock` endpoint to reject a zero `delta` before making any DB call. A zero adjustment is semantically meaningless and could mask client bugs. The test `AdjustStockAsync_WhenDeltaIsZero_ReturnsFailureWithoutCallingRepo` verifies no repository interaction occurs for this invalid input.

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

**AI produced:** A `rabbitmq-definitions.json` file mounted into the RabbitMQ container via `docker-compose.yml`. The file pre-declares a `topic` exchange named `commerce_hub`, a durable queue named `order.created`, and a binding using routing key `order.created`.

**Why it was needed:** Without the queue declared in advance, RabbitMQ would discard any message published to the exchange before a consumer connected and declared the queue. Pre-configuring via definitions ensures messages are retained and visible in the Management UI at `http://localhost:15672` immediately after checkout, without requiring a running consumer.

---

### Integration Tests

**Requested:** Generate integration tests using Testcontainers to spin up real MongoDB and RabbitMQ instances in Docker, testing the full stack from HTTP request to database state.

**AI produced:** An integration test suite (`tests/CommerceHub.Tests/Integration/`) covering checkout happy path, idempotency replay, insufficient-stock rejection, stock adjustment, order status transitions, and the Shipped terminal-state guard. Tests use `WebApplicationFactory<Program>` with Testcontainers-managed containers — every assertion is against real MongoDB documents.

---

### Manual Testing Guide

**Requested:** Generate a comprehensive manual testing guide with exact curl commands covering all happy paths and edge cases for every endpoint.

**AI produced:** `manualTesting.txt` covering all four system actors (Customer, Warehouse Manager, Fulfillment Staff, Downstream Service) with curl commands for every scenario and a reset procedure (`docker-compose down -v && docker-compose up --build`) for restoring seed stock between sessions.

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
