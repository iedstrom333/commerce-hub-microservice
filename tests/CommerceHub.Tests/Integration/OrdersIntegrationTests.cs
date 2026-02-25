using System.Net;
using System.Net.Http.Json;
using CommerceHub.Api.DTOs;
using CommerceHub.Api.Models;
using FluentAssertions;
using MongoDB.Driver;
using NUnit.Framework;

namespace CommerceHub.Tests.Integration;

[TestFixture]
public class OrdersIntegrationTests : IntegrationTestBase
{
    // ----------------------------------------------------------------
    // HELPERS
    // ----------------------------------------------------------------

    private static CheckoutRequestDto OneWidget(int qty = 1) => new()
    {
        CustomerId = "CUST-001",
        Items      = [new CheckoutItemDto { ProductId = "000000000000000000000001", Quantity = qty }]
    };

    private async Task<OrderResponseDto> CheckoutAsync(
        CheckoutRequestDto dto, string? idempotencyKey = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders/checkout")
        {
            Content = JsonContent.Create(dto)
        };
        if (idempotencyKey is not null)
            req.Headers.Add("Idempotency-Key", idempotencyKey);

        var response = await Client.SendAsync(req);
        response.IsSuccessStatusCode.Should().BeTrue(
            $"checkout failed with {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<OrderResponseDto>())!;
    }

    // ----------------------------------------------------------------
    // TEST 1: GET /api/orders returns 200 with an empty list initially
    // ----------------------------------------------------------------
    [Test]
    public async Task GetOrders_WhenNoOrders_Returns200WithEmptyList()
    {
        var response = await Client.GetAsync("/api/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var orders = await response.Content.ReadFromJsonAsync<List<OrderResponseDto>>();
        orders.Should().BeEmpty();
    }

    // ----------------------------------------------------------------
    // TEST 2: POST /api/orders/checkout with valid body returns 201
    // ----------------------------------------------------------------
    [Test]
    public async Task Checkout_WithValidRequest_Returns201WithCreatedOrder()
    {
        var request = new CheckoutRequestDto
        {
            CustomerId = "CUST-001",
            Items      = [new CheckoutItemDto { ProductId = "000000000000000000000001", Quantity = 2 }]
        };

        var response = await Client.PostAsJsonAsync("/api/orders/checkout", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var order = await response.Content.ReadFromJsonAsync<OrderResponseDto>();
        order.Should().NotBeNull();
        order!.CustomerId.Should().Be("CUST-001");
        order.Status.Should().Be(OrderStatus.Pending);
        order.Id.Should().NotBeNullOrEmpty();
    }

    // ----------------------------------------------------------------
    // TEST 3: Stock is decremented by the exact requested quantity
    //         in the real MongoDB collection
    // ----------------------------------------------------------------
    [Test]
    public async Task Checkout_WhenSuccessful_DecrementsStockInDatabase()
    {
        await CheckoutAsync(OneWidget(qty: 3));

        var product = await Db.GetCollection<Product>("Products")
            .Find(Builders<Product>.Filter.Eq(p => p.Id, "000000000000000000000001"))
            .FirstOrDefaultAsync();

        product!.StockQuantity.Should().Be(97); // 100 - 3
    }

    // ----------------------------------------------------------------
    // TEST 4: A StockDecremented audit entry is written after checkout
    //         — regression test for the CancellationToken fire-and-forget bug
    // ----------------------------------------------------------------
    [Test]
    public async Task Checkout_WhenSuccessful_WritesStockDecrementedAuditLog()
    {
        await CheckoutAsync(OneWidget(qty: 5));

        var log = await WaitForAuditLogAsync("StockDecremented", "000000000000000000000001");

        log.Should().NotBeNull("the StockDecremented audit entry must be written after checkout");
        log!.Actor.Should().Be("Checkout");
        log.Delta.Should().Be(-5);
        log.StockBefore.Should().Be(100);
        log.StockAfter.Should().Be(95);
        log.RelatedOrderId.Should().NotBeNullOrEmpty();
    }

    // ----------------------------------------------------------------
    // TEST 5: Checkout with insufficient stock returns 422
    // ----------------------------------------------------------------
    [Test]
    public async Task Checkout_WhenInsufficientStock_Returns422()
    {
        var request = new CheckoutRequestDto
        {
            CustomerId = "CUST-001",
            // Thingamajig Elite has only 5 units
            Items = [new CheckoutItemDto { ProductId = "000000000000000000000003", Quantity = 10 }]
        };

        var response = await Client.PostAsJsonAsync("/api/orders/checkout", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ----------------------------------------------------------------
    // TEST 6: Idempotent retry with the same key returns the same order
    //         without creating a second order or decrementing stock twice
    // ----------------------------------------------------------------
    [Test]
    public async Task Checkout_WithDuplicateIdempotencyKey_ReturnsSameOrderAndDecrementStockOnce()
    {
        const string key = "idem-integration-001";

        var order1 = await CheckoutAsync(OneWidget(qty: 1), idempotencyKey: key);
        var order2 = await CheckoutAsync(OneWidget(qty: 1), idempotencyKey: key);

        order1.Id.Should().Be(order2.Id,
            "same idempotency key must return the same order on replay");

        var product = await Db.GetCollection<Product>("Products")
            .Find(Builders<Product>.Filter.Eq(p => p.Id, "000000000000000000000001"))
            .FirstOrDefaultAsync();

        product!.StockQuantity.Should().Be(99,
            "stock should be decremented only once despite two requests");
    }

    // ----------------------------------------------------------------
    // TEST 7: GET /api/orders/{id} returns 404 for a nonexistent order
    // ----------------------------------------------------------------
    [Test]
    public async Task GetOrderById_WhenNotFound_Returns404()
    {
        var response = await Client.GetAsync("/api/orders/000000000000000000000099");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----------------------------------------------------------------
    // TEST 8: GET /api/orders/{id} returns the order after checkout
    // ----------------------------------------------------------------
    [Test]
    public async Task GetOrderById_AfterCheckout_Returns200WithOrder()
    {
        var created = await CheckoutAsync(OneWidget());

        var response = await Client.GetAsync($"/api/orders/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var order = await response.Content.ReadFromJsonAsync<OrderResponseDto>();
        order!.Id.Should().Be(created.Id);
    }

    // ----------------------------------------------------------------
    // TEST 9: PUT /api/orders/{id} — valid transition Pending → Processing
    // ----------------------------------------------------------------
    [Test]
    public async Task UpdateOrder_WhenTransitionIsValid_Returns200WithNewStatus()
    {
        var created = await CheckoutAsync(OneWidget());

        var dto = new UpdateOrderDto
        {
            CustomerId = "CUST-001",
            Status     = OrderStatus.Processing,
            Items      = [new CheckoutItemDto { ProductId = "000000000000000000000001", Quantity = 1 }]
        };

        var response = await Client.PutAsJsonAsync($"/api/orders/{created.Id}", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<OrderResponseDto>();
        updated!.Status.Should().Be(OrderStatus.Processing);
    }

    // ----------------------------------------------------------------
    // TEST 10: PUT /api/orders/{id} — invalid transition Pending → Shipped
    //          returns 409 Conflict
    // ----------------------------------------------------------------
    [Test]
    public async Task UpdateOrder_WhenTransitionIsInvalid_Returns409()
    {
        var created = await CheckoutAsync(OneWidget());

        var dto = new UpdateOrderDto
        {
            CustomerId = "CUST-001",
            Status     = OrderStatus.Shipped, // Pending → Shipped is not a valid transition
            Items      = [new CheckoutItemDto { ProductId = "000000000000000000000001", Quantity = 1 }]
        };

        var response = await Client.PutAsJsonAsync($"/api/orders/{created.Id}", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ----------------------------------------------------------------
    // TEST 11: GET /api/orders?customerId= filters by customer
    // ----------------------------------------------------------------
    [Test]
    public async Task GetOrders_WithCustomerIdFilter_ReturnsOnlyThatCustomersOrders()
    {
        await CheckoutAsync(new CheckoutRequestDto
        {
            CustomerId = "CUST-A",
            Items      = [new CheckoutItemDto { ProductId = "000000000000000000000001", Quantity = 1 }]
        });

        await CheckoutAsync(new CheckoutRequestDto
        {
            CustomerId = "CUST-B",
            Items      = [new CheckoutItemDto { ProductId = "000000000000000000000002", Quantity = 1 }]
        });

        var response = await Client.GetAsync("/api/orders?customerId=CUST-A");
        var orders   = await response.Content.ReadFromJsonAsync<List<OrderResponseDto>>();

        orders.Should().HaveCount(1);
        orders![0].CustomerId.Should().Be("CUST-A");
    }
}
