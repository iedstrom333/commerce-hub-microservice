# Commerce Hub Microservice

A production-style backend microservice built with **.NET 8**, **MongoDB**, and **RabbitMQ** that manages orders and inventory with atomic stock management.

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 8 / ASP.NET Core |
| Database | MongoDB 7 (atomic `FindOneAndUpdateAsync`) |
| Messaging | RabbitMQ 3.13 (durable topic exchange) |
| Testing | NUnit 3 + NSubstitute + FluentAssertions + Testcontainers |
| Containers | Docker Compose |

---

## Quick Start

**Prerequisites:** Docker and Docker Compose installed.

```bash
git clone https://github.com/iedstrom333/commerce-hub-microservice.git
cd commerce-hub-microservice
docker-compose up --build
```

That's it. All four services start in the correct order via health-check dependencies.

| Service | URL |
|---------|-----|
| API + Swagger UI | http://localhost:8080/swagger |
| Health Check | http://localhost:8080/health |
| RabbitMQ Management | http://localhost:15672 (guest / guest) |
| MongoDB | localhost:27017 |

---

## API Endpoints

### GET `/api/products`

Returns all products sorted by name.

```bash
curl http://localhost:8080/api/products
```

**Responses:**
- `200 OK` — array of product objects (`id`, `name`, `sku`, `price`, `stockQuantity`)

---

### PATCH `/api/products/{id}/stock`

Atomically adjusts product stock. Use a positive `delta` to restock, negative to decrement. Prevents stock from going below zero.

```bash
# Decrement by 5
curl -X PATCH http://localhost:8080/api/products/000000000000000000000001/stock \
  -H "Content-Type: application/json" \
  -d '{ "delta": -5 }'

# Restock by 50
curl -X PATCH http://localhost:8080/api/products/000000000000000000000001/stock \
  -H "Content-Type: application/json" \
  -d '{ "delta": 50 }'
```

**Responses:**
- `200 OK` — updated stock level
- `404 Not Found` — product does not exist
- `422 Unprocessable Entity` — adjustment would cause negative stock, or delta is zero

---

### GET `/api/orders`

Returns all orders sorted by creation date (newest first). Optionally filter by customer.

```bash
# All orders
curl http://localhost:8080/api/orders

# Orders for a specific customer
curl "http://localhost:8080/api/orders?customerId=CUST-001"
```

**Responses:**
- `200 OK` — array of order objects

---

### POST `/api/orders/checkout`

Processes a new order. Verifies stock levels, atomically decrements inventory, creates the order, and publishes an `OrderCreated` event to RabbitMQ.

Supports idempotent retries via an `Idempotency-Key` header. If a checkout request is retried with the same key, the original order is returned without re-processing.

```bash
curl -X POST http://localhost:8080/api/orders/checkout \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: a3f1c2d4-e5b6-7890-abcd-ef1234567890" \
  -d '{
    "customerId": "CUST-001",
    "items": [
      { "productId": "000000000000000000000001", "quantity": 2 },
      { "productId": "000000000000000000000002", "quantity": 1 }
    ]
  }'
```

**Responses:**
- `201 Created` — order created (or idempotent replay of a previous order with the same key)
- `422 Unprocessable Entity` — insufficient stock or invalid quantity
- `400 Bad Request` — missing required fields

---

### GET `/api/orders/{id}`

Retrieves a specific order by ID.

```bash
curl http://localhost:8080/api/orders/{orderId}
```

**Responses:**
- `200 OK` — order object
- `404 Not Found` — order does not exist

---

### PUT `/api/orders/{id}`

Full replacement of an order. Status transitions are validated by a state machine — only valid progressions are accepted.

**Valid transitions:**

| From | To |
|------|----|
| `Pending` | `Processing`, `Cancelled` |
| `Processing` | `Shipped`, `Cancelled` |
| `Shipped` | — (terminal) |
| `Cancelled` | — (terminal) |

```bash
curl -X PUT http://localhost:8080/api/orders/{orderId} \
  -H "Content-Type: application/json" \
  -d '{
    "customerId": "CUST-001",
    "status": "Processing",
    "items": [
      { "productId": "000000000000000000000001", "quantity": 2 }
    ]
  }'
```

**Responses:**
- `200 OK` — updated order
- `404 Not Found` — order does not exist
- `409 Conflict` — transition is not permitted (e.g. `Cancelled → Processing`)
- `400 Bad Request` — `status` is not one of `Pending`, `Processing`, `Shipped`, `Cancelled`

---

### GET `/health`

Reports the health of the API and its MongoDB dependency.

```bash
curl http://localhost:8080/health
```

**Responses:**
- `200 OK` — `{"status":"Healthy"}`
- `503 Service Unavailable` — MongoDB unreachable

---

## Audit Logs

Every meaningful state change is written to the `AuditLogs` MongoDB collection. Audit writes are fire-and-forget — a write failure never affects the primary operation.

| Event | Trigger | Actor |
|-------|---------|-------|
| `StockDecremented` | Successful checkout (per item) | Checkout |
| `StockRolledBack` | Failed checkout rollback (per item) | Checkout |
| `StockAdjusted` | `PATCH /api/products/{id}/stock` | Warehouse |
| `OrderStatusChanged` | `PUT /api/orders/{id}` | Fulfillment |

```bash
# Inspect audit logs
docker exec -it commerce_hub_mongo mongosh
use CommerceHub
db.AuditLogs.find().sort({ timestamp: -1 }).pretty()

# Filter by event type
db.AuditLogs.find({ event: "StockDecremented" }).pretty()

# All events linked to a specific order
db.AuditLogs.find({ relatedOrderId: "<orderId>" }).pretty()
```

---

## Seed Data

The `mongo-seed` container automatically inserts three test products on startup:

| ID | Name | Stock | Price |
|----|------|-------|-------|
| `000000000000000000000001` | Widget Pro | 100 | $29.99 |
| `000000000000000000000002` | Gadget Basic | 50 | $14.50 |
| `000000000000000000000003` | Thingamajig Elite | 5 | $89.00 |

Product 3 has intentionally low stock to test the insufficient-stock path.

---

## Running Tests

### Unit tests (no Docker socket needed)

```bash
docker run --rm \
  -v "$(pwd):/app" \
  -w /app \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/CommerceHub.Tests/ --filter "Category!=Integration"
```

**Expected: 25 tests, 25 passed, 0 failed**

### Integration tests (requires Docker socket)

Integration tests spin up real MongoDB and RabbitMQ containers via [Testcontainers](https://dotnet.testcontainers.org/) and run the full API through `WebApplicationFactory`.

```bash
docker run --rm \
  -v "$(pwd):/app" \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -e TESTCONTAINERS_RYUK_DISABLED=true \
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal \
  -w /app \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/CommerceHub.Tests/ --filter "Category=Integration"
```

**Expected: 18 tests, 18 passed, 0 failed**

Or run everything at once (requires Docker socket):

```bash
docker run --rm \
  -v "$(pwd):/app" \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -e TESTCONTAINERS_RYUK_DISABLED=true \
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal \
  -w /app \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/CommerceHub.Tests/
```

**Expected: 43 tests, 43 passed, 0 failed**

### Test Coverage

| Suite | Type | Tests |
|-------|------|-------|
| **Checkout — happy path** | Unit | Exact quantity decrement, event published, `StockDecremented` audit written with correct fields |
| **Checkout — failure** | Unit | Mid-checkout rollback, `StockRolledBack` audit written, order never created |
| **Checkout — resilience** | Unit | RabbitMQ publish failure does not roll back a committed order |
| **Checkout — idempotency** | Unit | Duplicate key returns cached order without touching stock; new key stores mapping |
| **Order update — state machine** | Unit | 4 valid transitions confirmed; 4 invalid transitions blocked with descriptive error |
| **Order update — audit** | Unit | `OrderStatusChanged` entry written with correct old/new status |
| **Order update — guards** | Unit | NOT_FOUND and terminal-state cases handled |
| **Stock adjustment** | Unit | Zero delta, negative exceeding stock, product not found, success with `StockAdjusted` audit |
| **Orders — HTTP + MongoDB** | Integration | Checkout 201, stock decrement verified in DB, `StockDecremented` audit polled from DB, insufficient-stock 422, idempotent replay, GET / GET by ID / PUT transitions / 409 conflict / customer filter |
| **Products — HTTP + MongoDB** | Integration | GET all, positive delta + DB verify, `StockAdjusted` audit polled from DB, zero delta 422, negative-exceeds-stock 422, not-found 404, health check |

---

## Architecture

```
Controllers  (thin — validates HTTP, delegates to services)
    ↓
Services     (all business logic: checkout, rollback, state machine, idempotency)
    ↓
Repositories (pure MongoDB operations, no business logic)
    ↓
MongoDB      (atomic FindOneAndUpdateAsync with filter-based stock guards)
    + RabbitMQ (durable topic exchange, persistent messages)
```

### Concurrency Design

Stock management is race-condition-safe via MongoDB's single-document atomic operations:

```
FindOneAndUpdateAsync(
  filter: { _id: productId, stockQuantity: { $gte: requestedQty } },
  update: { $inc: { stockQuantity: -requestedQty } }
)
```

If two concurrent requests compete for the last 3 units, MongoDB serializes them. Only one matches the `$gte` filter and succeeds; the other receives `null`, which the service interprets as insufficient stock.

### Checkout Rollback

Since the Docker Compose environment uses a standalone MongoDB instance (no replica set), multi-document ACID transactions are not available. A compensating transaction pattern is used instead:

1. Atomically decrement each item's stock in sequence
2. Track which items were decremented (including post-decrement stock level)
3. If any item fails (null return), re-increment all previously decremented items
4. Only create the order record after all decrements succeed

### Order Status State Machine

Status transitions are enforced in both the service layer (descriptive error messages) and the MongoDB `ReplaceAsync` filter (race-condition safety net):

```
Pending ──► Processing ──► Shipped (terminal)
   │               │
   └───────────────┴──► Cancelled (terminal)
```

### Idempotency

Checkout requests may include an `Idempotency-Key` header. The key is stored as the MongoDB `_id` of an `IdempotencyKeys` document (unique by design). A 24-hour TTL index expires old keys automatically. Concurrent duplicate requests are handled by catching MongoDB error 11000 on insert.

### MongoDB Indexes

Indexes are created idempotently at startup via `EnsureIndexesAsync`:

| Collection | Index | Purpose |
|------------|-------|---------|
| `AuditLogs` | `entityId asc, timestamp desc` | Audit history queries |
| `AuditLogs` | `relatedOrderId` (sparse) | Order-scoped audit lookups |
| `Orders` | `customerId` | `GET /api/orders?customerId=` filter |
| `Products` | `sku` (unique) | Data integrity — prevents duplicate SKUs |
| `Products` | `stockQuantity` | Stock-guard filter performance on checkout |
| `IdempotencyKeys` | `createdAt` (TTL 24h) | Automatic expiry of old keys |

---

## Project Structure

```
├── src/CommerceHub.Api/
│   ├── Controllers/       OrdersController, ProductsController
│   ├── Services/          OrderService, ProductService (business logic)
│   ├── Repositories/      OrderRepository, ProductRepository,
│   │                      AuditRepository, IdempotencyRepository
│   ├── Messaging/         RabbitMqEventPublisher
│   ├── Models/            Order, OrderItem, Product, AuditLog, IdempotencyKey
│   ├── DTOs/              Request/response data transfer objects
│   ├── Events/            OrderCreatedEvent
│   ├── Interfaces/        All service and repository contracts
│   ├── Common/            Result<T> type for error handling without exceptions
│   ├── Configuration/     MongoDbSettings, RabbitMqSettings
│   ├── Extensions/        ServiceCollectionExtensions, MongoIndexExtensions
│   ├── HealthChecks/      MongoHealthCheck
│   └── Middleware/        GlobalExceptionHandler
├── tests/CommerceHub.Tests/
│   ├── Services/          OrderServiceTests (14 tests), ProductServiceTests (5 tests)
│   ├── Integration/       GlobalTestSetup, IntegrationTestBase,
│   │                      OrdersIntegrationTests (11 tests), ProductsIntegrationTests (7 tests)
│   └── Helpers/           TestDataBuilder
├── mongo-seed/            seed.js with 3 test products
├── docker-compose.yml
├── sequence-diagrams.md
├── use-case-diagrams.md
└── developer-log.md
```
