# Commerce Hub — Sequence Diagrams

Five distinct operation types are supported. Each diagram shows the full call stack from
external actor through controller → service → repository → MongoDB/RabbitMQ, including
the fire-and-forget audit writes added in the latest implementation.

---

## 1. Successful Checkout

`POST /api/orders/checkout` where every item has sufficient stock.

```mermaid
sequenceDiagram
    actor Customer
    participant OrdersController
    participant OrderService
    participant ProductRepo
    participant OrderRepo
    participant AuditRepo
    participant MongoDB
    participant RabbitMQ

    Customer->>OrdersController: POST /api/orders/checkout
    OrdersController->>OrderService: CheckoutAsync(dto)

    loop For each item in dto.Items
        OrderService->>ProductRepo: DecrementStockAtomicAsync(productId, qty)
        ProductRepo->>MongoDB: FindOneAndUpdate<br/>(filter: id & stock ≥ qty, inc: −qty, ReturnDocument.After)
        MongoDB-->>ProductRepo: updated product (stockAfter)
        ProductRepo-->>OrderService: product
        Note over OrderService: append (productId, qty, stockAfter) to decremented list
    end

    OrderService->>OrderRepo: CreateAsync(order)
    OrderRepo->>MongoDB: InsertOne(order)
    MongoDB-->>OrderRepo: order with generated Id
    OrderRepo-->>OrderService: created order

    loop For each item in decremented list
        OrderService-)AuditRepo: LogAsync(StockDecremented) [fire-and-forget]
        AuditRepo-)MongoDB: InsertOne(AuditLog)<br/>event=StockDecremented, actor=Checkout<br/>delta=−qty, stockBefore, stockAfter, relatedOrderId
    end

    OrderService->>RabbitMQ: PublishAsync("order.created", OrderCreatedEvent)
    RabbitMQ-->>OrderService: ack

    OrderService-->>OrdersController: Result.Ok(OrderResponseDto)
    OrdersController-->>Customer: 201 Created + OrderResponseDto
```

---

## 2. Failed Checkout — Insufficient Stock / Product Not Found

`POST /api/orders/checkout` where a later item's atomic decrement returns null.
All previously decremented stock is rolled back.

```mermaid
sequenceDiagram
    actor Customer
    participant OrdersController
    participant OrderService
    participant ProductRepo
    participant AuditRepo
    participant MongoDB

    Customer->>OrdersController: POST /api/orders/checkout<br/>(item1: qty=5, item2: qty=999)
    OrdersController->>OrderService: CheckoutAsync(dto)

    OrderService->>ProductRepo: DecrementStockAtomicAsync(item1, 5)
    ProductRepo->>MongoDB: FindOneAndUpdate (stock ≥ 5, inc: −5)
    MongoDB-->>ProductRepo: updated product
    ProductRepo-->>OrderService: product (stockAfter = N−5)
    Note over OrderService: decremented = [(item1, 5, N−5)]

    OrderService->>ProductRepo: DecrementStockAtomicAsync(item2, 999)
    ProductRepo->>MongoDB: FindOneAndUpdate (stock ≥ 999, inc: −999)
    MongoDB-->>ProductRepo: null (filter did not match)
    ProductRepo-->>OrderService: null

    Note over OrderService: Rollback triggered for all items in decremented list

    OrderService->>ProductRepo: IncrementStockAsync(item1, 5)
    ProductRepo->>MongoDB: UpdateOne (inc: +5)
    MongoDB-->>ProductRepo: ok
    OrderService-)AuditRepo: LogAsync(StockRolledBack) [fire-and-forget]
    AuditRepo-)MongoDB: InsertOne(AuditLog)<br/>event=StockRolledBack, actor=Checkout, delta=+5

    OrderService-->>OrdersController: Result.Fail("Insufficient stock or product not found: item2")
    OrdersController-->>Customer: 422 Unprocessable Entity
```

---

## 3. Stock Adjustment (Warehouse)

`PATCH /api/products/{id}/stock` — warehouse manager restocks or manually decrements inventory.

```mermaid
sequenceDiagram
    actor WarehouseManager as Warehouse Manager
    participant ProductsController
    participant ProductService
    participant ProductRepo
    participant AuditRepo
    participant MongoDB

    WarehouseManager->>ProductsController: PATCH /api/products/{id}/stock {delta: N}
    ProductsController->>ProductService: AdjustStockAsync(id, delta)

    ProductService->>ProductRepo: AdjustStockAtomicAsync(id, delta)
    ProductRepo->>MongoDB: FindOneAndUpdate<br/>(filter: id [+ stock ≥ −delta if delta < 0]<br/>inc: delta, ReturnDocument.After)

    alt product found & stock constraint satisfied
        MongoDB-->>ProductRepo: updated product
        ProductRepo-->>ProductService: product (stockAfter)
        ProductService-)AuditRepo: LogAsync(StockAdjusted) [fire-and-forget]
        AuditRepo-)MongoDB: InsertOne(AuditLog)<br/>event=StockAdjusted, actor=Warehouse<br/>delta, stockBefore=stockAfter−delta, stockAfter
        ProductService-->>ProductsController: Result.Ok(ProductStockResponseDto)
        ProductsController-->>WarehouseManager: 200 OK + {id, name, stockQuantity}
    else product not found
        MongoDB-->>ProductRepo: null
        ProductRepo-->>ProductService: null
        ProductService->>ProductRepo: GetByIdAsync(id)
        ProductRepo->>MongoDB: Find(id)
        MongoDB-->>ProductRepo: null
        ProductRepo-->>ProductService: null
        ProductService-->>ProductsController: Result.Fail("NOT_FOUND")
        ProductsController-->>WarehouseManager: 404 Not Found
    else delta would cause negative stock
        MongoDB-->>ProductRepo: null
        ProductRepo-->>ProductService: null
        ProductService->>ProductRepo: GetByIdAsync(id)
        ProductRepo->>MongoDB: Find(id)
        MongoDB-->>ProductRepo: existing product
        ProductRepo-->>ProductService: product (current stock)
        ProductService-->>ProductsController: Result.Fail("would cause negative stock. Current stock: X")
        ProductsController-->>WarehouseManager: 422 Unprocessable Entity
    end
```

---

## 4. Order Status Update (Fulfillment)

`PUT /api/orders/{id}` — fulfillment staff advances an order through its lifecycle.

```mermaid
sequenceDiagram
    actor FulfillmentStaff as Fulfillment Staff
    participant OrdersController
    participant OrderService
    participant OrderRepo
    participant AuditRepo
    participant MongoDB

    FulfillmentStaff->>OrdersController: PUT /api/orders/{id} {status: "Shipped", ...}
    OrdersController->>OrderService: UpdateAsync(id, dto)

    OrderService->>OrderRepo: GetByIdAsync(id)
    OrderRepo->>MongoDB: Find(id)

    alt order not found
        MongoDB-->>OrderRepo: null
        OrderRepo-->>OrderService: null
        OrderService-->>OrdersController: Result.Fail("NOT_FOUND")
        OrdersController-->>FulfillmentStaff: 404 Not Found
    else current status == Shipped
        MongoDB-->>OrderRepo: order (status=Shipped)
        OrderRepo-->>OrderService: order
        OrderService-->>OrdersController: Result.Fail("cannot be modified once Shipped")
        OrdersController-->>FulfillmentStaff: 409 Conflict
    else order exists & not yet Shipped
        MongoDB-->>OrderRepo: order
        OrderRepo-->>OrderService: existing order (oldStatus captured)
        OrderService->>OrderRepo: ReplaceAsync(id, updatedOrder)
        OrderRepo->>MongoDB: FindOneAndReplace<br/>(filter: id & status ≠ Shipped, ReturnDocument.After)
        MongoDB-->>OrderRepo: saved order
        OrderRepo-->>OrderService: saved order
        OrderService-)AuditRepo: LogAsync(OrderStatusChanged) [fire-and-forget]
        AuditRepo-)MongoDB: InsertOne(AuditLog)<br/>event=OrderStatusChanged, actor=Fulfillment<br/>oldStatus, newStatus
        OrderService-->>OrdersController: Result.Ok(OrderResponseDto)
        OrdersController-->>FulfillmentStaff: 200 OK + OrderResponseDto
    end
```

---

## 5. Get Order by ID

`GET /api/orders/{id}` — customer or fulfillment staff polls order state.

```mermaid
sequenceDiagram
    actor Customer
    participant OrdersController
    participant OrderService
    participant OrderRepo
    participant MongoDB

    Customer->>OrdersController: GET /api/orders/{id}
    OrdersController->>OrderService: GetByIdAsync(id)
    OrderService->>OrderRepo: GetByIdAsync(id)
    OrderRepo->>MongoDB: Find(id)

    alt order found
        MongoDB-->>OrderRepo: order document
        OrderRepo-->>OrderService: order
        OrderService-->>OrdersController: OrderResponseDto
        OrdersController-->>Customer: 200 OK + OrderResponseDto
    else not found
        MongoDB-->>OrderRepo: null
        OrderRepo-->>OrderService: null
        OrderService-->>OrdersController: null
        OrdersController-->>Customer: 404 Not Found
    end
```

---

## 6. Downstream Event Consumption

After a successful checkout, an `OrderCreated` event is published to RabbitMQ.
Any downstream service bound to the `commerce_hub` exchange can consume it.

```mermaid
sequenceDiagram
    participant RabbitMQ
    actor DownstreamService as Downstream Service

    Note over RabbitMQ: exchange=commerce_hub (topic, durable)<br/>routing key=order.created<br/>payload=OrderCreatedEvent JSON

    RabbitMQ--)DownstreamService: deliver message (order.created)
    Note over DownstreamService: Process: fulfilment pipeline,<br/>email notification, analytics, etc.
    DownstreamService->>RabbitMQ: basicAck
```
