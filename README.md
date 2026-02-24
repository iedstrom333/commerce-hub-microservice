# Commerce Hub Microservice

A production-style backend microservice built with **.NET 8**, **MongoDB**, and **RabbitMQ** that manages orders and inventory with atomic stock management.

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 8 / ASP.NET Core |
| Database | MongoDB 7 (atomic `FindOneAndUpdateAsync`) |
| Messaging | RabbitMQ 3.13 (durable topic exchange) |
| Testing | nUnit 4 + NSubstitute + FluentAssertions |
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
| RabbitMQ Management | http://localhost:15672 (guest / guest) |
| MongoDB | localhost:27017 |

---

## API Endpoints

### POST `/api/orders/checkout`

Processes a new order. Verifies stock levels, atomically decrements inventory, creates the order, and publishes an `OrderCreated` event to RabbitMQ.

```bash
curl -X POST http://localhost:8080/api/orders/checkout \
  -H "Content-Type: application/json" \
  -d '{
    "customerId": "CUST-001",
    "items": [
      { "productId": "000000000000000000000001", "quantity": 2 },
      { "productId": "000000000000000000000002", "quantity": 1 }
    ]
  }'
```

**Responses:**
- `201 Created` — order created, body contains the order object
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

Idempotent full replacement of an order. Blocked if the order status is `Shipped`.

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
- `409 Conflict` — order is already Shipped

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

```bash
# Requires .NET 8 SDK installed locally
dotnet test tests/CommerceHub.Tests/

# Expected: 10 tests, 10 passed, 0 failed
```

### Test Coverage

| Test | Scenario |
|------|----------|
| `CheckoutAsync_WhenQuantityIsZeroOrNegative_*` | Input validation without DB calls |
| `CheckoutAsync_WhenStockSufficient_*` | Exact quantity decrement verified |
| `CheckoutAsync_WhenSuccessful_*` | OrderCreated event published |
| `CheckoutAsync_WhenSecondItemOutOfStock_*` | Rollback of first item on mid-checkout failure |
| `UpdateAsync_WhenOrderIsShipped_*` | PUT blocked; ReplaceAsync never called |
| `GetByIdAsync_WhenOrderDoesNotExist_*` | Returns null correctly |
| `AdjustStockAsync_WhenDeltaIsZero_*` | Zero delta rejected without DB call |
| `AdjustStockAsync_WhenNegativeDeltaExceedsStock_*` | Negative-stock guard |
| `AdjustStockAsync_WhenPositiveDelta_*` | Positive restock succeeds |
| `AdjustStockAsync_WhenProductDoesNotExist_*` | Not found error |

---

## Architecture

```
Controllers (thin — delegates to services)
    ↓
Services (all business logic: checkout flow, rollback, event emission)
    ↓
Repositories (pure MongoDB operations, no business logic)
    ↓
MongoDB (atomic FindOneAndUpdateAsync with filter-based stock guards)
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
2. Track which items were decremented
3. If any item fails (null return), re-increment all previously decremented items
4. Only create the order record after all decrements succeed

---

## Project Structure

```
├── src/CommerceHub.Api/
│   ├── Controllers/       OrdersController, ProductsController
│   ├── Services/          OrderService, ProductService (business logic)
│   ├── Repositories/      OrderRepository, ProductRepository (MongoDB)
│   ├── Messaging/         RabbitMqEventPublisher
│   ├── Models/            Order, OrderItem, Product
│   ├── DTOs/              Request/response data transfer objects
│   ├── Events/            OrderCreatedEvent
│   ├── Interfaces/        All service and repository contracts
│   ├── Common/            Result<T> type for error handling without exceptions
│   ├── Configuration/     MongoDbSettings, RabbitMqSettings
│   └── Extensions/        ServiceCollectionExtensions (DI wiring)
├── tests/CommerceHub.Tests/
│   ├── Services/          OrderServiceTests, ProductServiceTests
│   └── Helpers/           TestDataBuilder
├── mongo-seed/            seed.js with 3 test products + indexes
├── docker-compose.yml
└── developer-log.md
```
