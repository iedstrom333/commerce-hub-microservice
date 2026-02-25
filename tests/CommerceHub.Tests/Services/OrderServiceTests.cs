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
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace CommerceHub.Tests.Services;

[TestFixture]
public class OrderServiceTests
{
    private IOrderRepository   _orderRepo   = null!;
    private IProductRepository _productRepo = null!;
    private IEventPublisher    _publisher   = null!;
    private IAuditRepository   _auditRepo   = null!;
    private OrderService       _sut         = null!;

    [SetUp]
    public void SetUp()
    {
        _orderRepo   = Substitute.For<IOrderRepository>();
        _productRepo = Substitute.For<IProductRepository>();
        _publisher   = Substitute.For<IEventPublisher>();
        _auditRepo   = Substitute.For<IAuditRepository>();

        var mqSettings = Options.Create(new RabbitMqSettings
        {
            Host                   = "localhost",
            Username               = "guest",
            Password               = "guest",
            ExchangeName           = "commerce_hub",
            OrderCreatedRoutingKey = "order.created"
        });

        _sut = new OrderService(
            _orderRepo,
            _productRepo,
            _publisher,
            mqSettings,
            _auditRepo,
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

        await _productRepo
            .DidNotReceive()
            .DecrementStockAtomicAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

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
        const int requested         = 3;
        var       decrementedProduct = TestDataBuilder.BuildProduct(stockQuantity: 97);

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

        await _productRepo
            .Received(1)
            .DecrementStockAtomicAsync(
                TestDataBuilder.ProductId1, requested, Arg.Any<CancellationToken>());

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
            .DecrementStockAtomicAsync(TestDataBuilder.ProductId1, 2, Arg.Any<CancellationToken>())
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

        _productRepo
            .DecrementStockAtomicAsync(TestDataBuilder.ProductId1, 2, Arg.Any<CancellationToken>())
            .Returns(product1);

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

        await _productRepo
            .Received(1)
            .IncrementStockAsync(TestDataBuilder.ProductId1, 2, Arg.Any<CancellationToken>());

        await _orderRepo
            .DidNotReceive()
            .CreateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());

        await _publisher
            .DidNotReceive()
            .PublishAsync(
                Arg.Any<string>(),
                Arg.Any<OrderCreatedEvent>(),
                Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 5: Rollback writes a StockRolledBack audit entry per item
    // ----------------------------------------------------------------
    [Test]
    public async Task CheckoutAsync_WhenRolledBack_WritesStockRolledBackAuditEntryForEachItem()
    {
        var product1 = TestDataBuilder.BuildProduct(id: TestDataBuilder.ProductId1, stockQuantity: 48);

        _productRepo
            .DecrementStockAtomicAsync(TestDataBuilder.ProductId1, 2, Arg.Any<CancellationToken>())
            .Returns(product1);

        _productRepo
            .DecrementStockAtomicAsync(TestDataBuilder.ProductId2, 5, Arg.Any<CancellationToken>())
            .Returns((Product?)null);

        await _sut.CheckoutAsync(new CheckoutRequestDto
        {
            CustomerId = "CUST-001",
            Items =
            [
                new CheckoutItemDto { ProductId = TestDataBuilder.ProductId1, Quantity = 2 },
                new CheckoutItemDto { ProductId = TestDataBuilder.ProductId2, Quantity = 5 }
            ]
        }, CancellationToken.None);

        await _auditRepo
            .Received(1)
            .LogAsync(
                Arg.Is<AuditLog>(l =>
                    l.Event     == "StockRolledBack" &&
                    l.Actor     == "Checkout"        &&
                    l.EntityId  == TestDataBuilder.ProductId1 &&
                    l.Delta     == 2),
                Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 6: Successful checkout writes a StockDecremented audit entry
    //         per item, populated with relatedOrderId and stock values
    // ----------------------------------------------------------------
    [Test]
    public async Task CheckoutAsync_WhenSuccessful_WritesStockDecrementedAuditEntryForEachItem()
    {
        var product = TestDataBuilder.BuildProduct(stockQuantity: 97); // stockAfter = 97, so stockBefore = 99

        _productRepo
            .DecrementStockAtomicAsync(TestDataBuilder.ProductId1, 2, Arg.Any<CancellationToken>())
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

        await _auditRepo
            .Received(1)
            .LogAsync(
                Arg.Is<AuditLog>(l =>
                    l.Event          == "StockDecremented"    &&
                    l.Actor          == "Checkout"            &&
                    l.EntityId       == TestDataBuilder.ProductId1 &&
                    l.Delta          == -2                    &&
                    l.StockAfter     == 97                    &&
                    l.StockBefore    == 99                    &&
                    l.RelatedOrderId == TestDataBuilder.OrderId1),
                Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 7: A RabbitMQ publish failure does not roll back a committed order
    // ----------------------------------------------------------------
    [Test]
    public async Task CheckoutAsync_WhenPublisherThrows_StillReturnsSuccessBecauseOrderIsCommitted()
    {
        var product = TestDataBuilder.BuildProduct();

        _productRepo
            .DecrementStockAtomicAsync(TestDataBuilder.ProductId1, 1, Arg.Any<CancellationToken>())
            .Returns(product);

        _orderRepo
            .CreateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var order = callInfo.Arg<Order>();
                order.Id = TestDataBuilder.OrderId1;
                return Task.FromResult(order);
            });

        _publisher
            .PublishAsync(Arg.Any<string>(), Arg.Any<OrderCreatedEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("RabbitMQ unavailable"));

        var result = await _sut.CheckoutAsync(
            TestDataBuilder.BuildCheckoutRequest(quantity: 1),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the order is already persisted; publish failure is best-effort");

        await _productRepo
            .DidNotReceive()
            .IncrementStockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 8: UpdateAsync returns NOT_FOUND when the order does not exist
    // ----------------------------------------------------------------
    [Test]
    public async Task UpdateAsync_WhenOrderDoesNotExist_ReturnsNotFoundError()
    {
        _orderRepo
            .GetByIdAsync("nonexistent-id", Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        var result = await _sut.UpdateAsync(
            "nonexistent-id",
            new UpdateOrderDto { CustomerId = "CUST-001", Items = [], Status = OrderStatus.Processing },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("NOT_FOUND");

        await _orderRepo
            .DidNotReceive()
            .ReplaceAsync(Arg.Any<string>(), Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 9: UpdateAsync is blocked when the current status is Shipped
    // ----------------------------------------------------------------
    [Test]
    public async Task UpdateAsync_WhenOrderIsShipped_ReturnsConflictErrorWithoutCallingReplace()
    {
        _orderRepo
            .GetByIdAsync(TestDataBuilder.OrderId1, Arg.Any<CancellationToken>())
            .Returns(TestDataBuilder.BuildOrder(status: OrderStatus.Shipped));

        var result = await _sut.UpdateAsync(
            TestDataBuilder.OrderId1,
            new UpdateOrderDto { CustomerId = "CUST-001", Items = [], Status = OrderStatus.Processing },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Shipped");

        await _orderRepo
            .DidNotReceive()
            .ReplaceAsync(Arg.Any<string>(), Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 10: State machine blocks invalid transitions
    //          e.g. Cancelled → Processing is not a valid move
    // ----------------------------------------------------------------
    [TestCase(OrderStatus.Cancelled, OrderStatus.Processing)]
    [TestCase(OrderStatus.Cancelled, OrderStatus.Shipped)]
    [TestCase(OrderStatus.Processing, OrderStatus.Pending)]
    [TestCase(OrderStatus.Shipped,    OrderStatus.Cancelled)]
    public async Task UpdateAsync_WhenTransitionIsInvalid_ReturnsConflictWithoutCallingReplace(
        string currentStatus, string requestedStatus)
    {
        _orderRepo
            .GetByIdAsync(TestDataBuilder.OrderId1, Arg.Any<CancellationToken>())
            .Returns(TestDataBuilder.BuildOrder(status: currentStatus));

        var result = await _sut.UpdateAsync(
            TestDataBuilder.OrderId1,
            new UpdateOrderDto { CustomerId = "CUST-001", Items = [], Status = requestedStatus },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain(currentStatus).And.Contain(requestedStatus);

        await _orderRepo
            .DidNotReceive()
            .ReplaceAsync(Arg.Any<string>(), Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 11: Valid transitions succeed and write an OrderStatusChanged audit entry
    // ----------------------------------------------------------------
    [TestCase(OrderStatus.Pending,    OrderStatus.Processing)]
    [TestCase(OrderStatus.Pending,    OrderStatus.Cancelled)]
    [TestCase(OrderStatus.Processing, OrderStatus.Shipped)]
    [TestCase(OrderStatus.Processing, OrderStatus.Cancelled)]
    public async Task UpdateAsync_WhenTransitionIsValid_ReturnsSuccessAndLogsOrderStatusChangedAudit(
        string currentStatus, string requestedStatus)
    {
        var existingOrder = TestDataBuilder.BuildOrder(status: currentStatus);
        var savedOrder    = TestDataBuilder.BuildOrder(status: requestedStatus);

        _orderRepo
            .GetByIdAsync(TestDataBuilder.OrderId1, Arg.Any<CancellationToken>())
            .Returns(existingOrder);

        _orderRepo
            .ReplaceAsync(TestDataBuilder.OrderId1, Arg.Any<Order>(), Arg.Any<CancellationToken>())
            .Returns(savedOrder);

        var result = await _sut.UpdateAsync(
            TestDataBuilder.OrderId1,
            new UpdateOrderDto { CustomerId = "CUST-001", Items = [], Status = requestedStatus },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await _auditRepo
            .Received(1)
            .LogAsync(
                Arg.Is<AuditLog>(l =>
                    l.Event     == "OrderStatusChanged" &&
                    l.Actor     == "Fulfillment"        &&
                    l.EntityId  == TestDataBuilder.OrderId1 &&
                    l.OldStatus == currentStatus        &&
                    l.NewStatus == requestedStatus),
                Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 12: GET returns null when order does not exist
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
