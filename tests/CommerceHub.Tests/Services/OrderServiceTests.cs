using CommerceHub.Api.Configuration;
using CommerceHub.Api.DTOs;
using CommerceHub.Api.Events;
using CommerceHub.Api.Interfaces;
using CommerceHub.Api.Models;
using CommerceHub.Api.Services;
using CommerceHub.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;

namespace CommerceHub.Tests.Services;

[TestFixture]
public class OrderServiceTests
{
    private IOrderRepository _orderRepo = null!;
    private IProductRepository _productRepo = null!;
    private IEventPublisher _publisher = null!;
    private OrderService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _orderRepo   = Substitute.For<IOrderRepository>();
        _productRepo = Substitute.For<IProductRepository>();
        _publisher   = Substitute.For<IEventPublisher>();

        var mqSettings = Options.Create(new RabbitMqSettings
        {
            Host     = "localhost",
            Username = "guest",
            Password = "guest",
            ExchangeName            = "commerce_hub",
            OrderCreatedRoutingKey  = "order.created"
        });

        _sut = new OrderService(
            _orderRepo,
            _productRepo,
            _publisher,
            mqSettings,
            Substitute.For<ILogger<OrderService>>());
    }

    // ----------------------------------------------------------------
    // TEST 1: Negative quantity is rejected before touching the database
    // ----------------------------------------------------------------
    [Test]
    public async Task CheckoutAsync_WhenQuantityIsZeroOrNegative_ReturnsFailureWithoutCallingRepo()
    {
        var request = new CheckoutRequestDto
        {
            CustomerId = "CUST-001",
            Items      = [new CheckoutItemDto { ProductId = TestDataBuilder.ProductId1, Quantity = -1 }]
        };

        var result = await _sut.CheckoutAsync(request, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Quantity");

        // The repository must never be touched for an invalid request
        await _productRepo
            .DidNotReceive()
            .DecrementStockAtomicAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>());

        await _orderRepo
            .DidNotReceive()
            .CreateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 2: Stock is decremented by the exact requested quantity
    // ----------------------------------------------------------------
    [Test]
    public async Task CheckoutAsync_WhenStockSufficient_DecrementsExactQuantityAndCreatesOrder()
    {
        const int requested = 3;
        var product = TestDataBuilder.BuildProduct(stockQuantity: 100);
        var decrementedProduct = TestDataBuilder.BuildProduct(stockQuantity: 97);

        _productRepo
            .DecrementStockAtomicAsync(TestDataBuilder.ProductId1, requested, Arg.Any<CancellationToken>())
            .Returns(decrementedProduct);

        _orderRepo
            .CreateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var order = callInfo.Arg<Order>();
                order.Id = TestDataBuilder.OrderId1;
                return Task.FromResult(order);
            });

        var result = await _sut.CheckoutAsync(
            TestDataBuilder.BuildCheckoutRequest(quantity: requested),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OrderStatus.Pending);

        // Verify decrement was called with the precise quantity
        await _productRepo
            .Received(1)
            .DecrementStockAtomicAsync(
                TestDataBuilder.ProductId1,
                requested,
                Arg.Any<CancellationToken>());

        await _orderRepo
            .Received(1)
            .CreateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 3: OrderCreated event is published after a successful checkout
    // ----------------------------------------------------------------
    [Test]
    public async Task CheckoutAsync_WhenSuccessful_PublishesOrderCreatedEventWithCorrectOrderId()
    {
        var product = TestDataBuilder.BuildProduct();

        _productRepo
            .DecrementStockAtomicAsync(
                TestDataBuilder.ProductId1, 2, Arg.Any<CancellationToken>())
            .Returns(product);

        _orderRepo
            .CreateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var order = callInfo.Arg<Order>();
                order.Id = TestDataBuilder.OrderId1;
                return Task.FromResult(order);
            });

        await _sut.CheckoutAsync(
            TestDataBuilder.BuildCheckoutRequest(quantity: 2),
            CancellationToken.None);

        // Publisher must be called exactly once with the correct routing key and order ID
        await _publisher
            .Received(1)
            .PublishAsync(
                Arg.Is<string>(k => k == "order.created"),
                Arg.Is<OrderCreatedEvent>(e => e.OrderId == TestDataBuilder.OrderId1),
                Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 4: Mid-checkout failure triggers rollback of already-decremented items
    // ----------------------------------------------------------------
    [Test]
    public async Task CheckoutAsync_WhenSecondItemOutOfStock_RollsBackFirstItemAndReturnsFailure()
    {
        var product1 = TestDataBuilder.BuildProduct(id: TestDataBuilder.ProductId1, stockQuantity: 98);

        // First product decrements successfully
        _productRepo
            .DecrementStockAtomicAsync(TestDataBuilder.ProductId1, 2, Arg.Any<CancellationToken>())
            .Returns(product1);

        // Second product has insufficient stock — returns null
        _productRepo
            .DecrementStockAtomicAsync(TestDataBuilder.ProductId2, 5, Arg.Any<CancellationToken>())
            .Returns((Product?)null);

        var request = new CheckoutRequestDto
        {
            CustomerId = "CUST-001",
            Items =
            [
                new CheckoutItemDto { ProductId = TestDataBuilder.ProductId1, Quantity = 2 },
                new CheckoutItemDto { ProductId = TestDataBuilder.ProductId2, Quantity = 5 }
            ]
        };

        var result = await _sut.CheckoutAsync(request, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain(TestDataBuilder.ProductId2);

        // Rollback: product1 must be re-incremented with the same quantity
        await _productRepo
            .Received(1)
            .IncrementStockAsync(TestDataBuilder.ProductId1, 2, Arg.Any<CancellationToken>());

        // Order must NOT be created when checkout fails
        await _orderRepo
            .DidNotReceive()
            .CreateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());

        // Event must NOT be published
        await _publisher
            .DidNotReceive()
            .PublishAsync(
                Arg.Any<string>(),
                Arg.Any<OrderCreatedEvent>(),
                Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 5: PUT update is blocked when order status is Shipped
    // ----------------------------------------------------------------
    [Test]
    public async Task UpdateAsync_WhenOrderIsShipped_ReturnsConflictErrorWithoutCallingReplace()
    {
        var shippedOrder = TestDataBuilder.BuildOrder(status: OrderStatus.Shipped);

        _orderRepo
            .GetByIdAsync(TestDataBuilder.OrderId1, Arg.Any<CancellationToken>())
            .Returns(shippedOrder);

        var result = await _sut.UpdateAsync(
            TestDataBuilder.OrderId1,
            new UpdateOrderDto
            {
                CustomerId = "CUST-001",
                Items      = [],
                Status     = OrderStatus.Processing
            },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Shipped");

        // ReplaceAsync must never be called for a Shipped order
        await _orderRepo
            .DidNotReceive()
            .ReplaceAsync(
                Arg.Any<string>(),
                Arg.Any<Order>(),
                Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 6: GET returns null when order does not exist
    // ----------------------------------------------------------------
    [Test]
    public async Task GetByIdAsync_WhenOrderDoesNotExist_ReturnsNull()
    {
        _orderRepo
            .GetByIdAsync("nonexistent-id", Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        var result = await _sut.GetByIdAsync("nonexistent-id", CancellationToken.None);

        result.Should().BeNull();
    }
}
