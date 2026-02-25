using CommerceHub.Api.Interfaces;
using CommerceHub.Api.Models;
using CommerceHub.Api.Services;
using CommerceHub.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace CommerceHub.Tests.Services;

[TestFixture]
public class ProductServiceTests
{
    private IProductRepository _productRepo = null!;
    private IAuditRepository   _auditRepo   = null!;
    private ProductService     _sut         = null!;

    [SetUp]
    public void SetUp()
    {
        _productRepo = Substitute.For<IProductRepository>();
        _auditRepo   = Substitute.For<IAuditRepository>();
        _sut = new ProductService(
            _productRepo,
            _auditRepo,
            Substitute.For<ILogger<ProductService>>());
    }

    // ----------------------------------------------------------------
    // TEST 1: Zero delta is rejected immediately without touching the repo
    // ----------------------------------------------------------------
    [Test]
    public async Task AdjustStockAsync_WhenDeltaIsZero_ReturnsFailureWithoutCallingRepo()
    {
        var result = await _sut.AdjustStockAsync(TestDataBuilder.ProductId1, 0, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("zero");

        await _productRepo
            .DidNotReceive()
            .AdjustStockAtomicAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------
    // TEST 2: Negative delta that would cause negative stock returns failure
    // ----------------------------------------------------------------
    [Test]
    public async Task AdjustStockAsync_WhenNegativeDeltaExceedsStock_ReturnsInsufficientStockError()
    {
        var existingProduct = TestDataBuilder.BuildProduct(stockQuantity: 10);

        _productRepo
            .AdjustStockAtomicAsync(TestDataBuilder.ProductId1, -50, Arg.Any<CancellationToken>())
            .Returns((Product?)null);

        _productRepo
            .GetByIdAsync(TestDataBuilder.ProductId1, Arg.Any<CancellationToken>())
            .Returns(existingProduct);

        var result = await _sut.AdjustStockAsync(TestDataBuilder.ProductId1, -50, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("stock");
    }

    // ----------------------------------------------------------------
    // TEST 3: Valid positive delta succeeds and returns updated stock
    // ----------------------------------------------------------------
    [Test]
    public async Task AdjustStockAsync_WhenPositiveDelta_ReturnsUpdatedStock()
    {
        var updatedProduct = TestDataBuilder.BuildProduct(stockQuantity: 110);

        _productRepo
            .AdjustStockAtomicAsync(TestDataBuilder.ProductId1, 10, Arg.Any<CancellationToken>())
            .Returns(updatedProduct);

        var result = await _sut.AdjustStockAsync(TestDataBuilder.ProductId1, 10, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.StockQuantity.Should().Be(110);
    }

    // ----------------------------------------------------------------
    // TEST 4: Product not found returns NOT_FOUND error
    // ----------------------------------------------------------------
    [Test]
    public async Task AdjustStockAsync_WhenProductDoesNotExist_ReturnsNotFoundError()
    {
        _productRepo
            .AdjustStockAtomicAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((Product?)null);

        _productRepo
            .GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Product?)null);

        var result = await _sut.AdjustStockAsync("nonexistent-id", -5, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("NOT_FOUND");
    }

    // ----------------------------------------------------------------
    // TEST 5: Successful adjustment writes a StockAdjusted audit entry
    //         with correct before/after stock values
    // ----------------------------------------------------------------
    [Test]
    public async Task AdjustStockAsync_WhenSuccessful_LogsStockAdjustedAuditWithCorrectFields()
    {
        // stockAfter = 110, delta = 10, so stockBefore = 100
        var updatedProduct = TestDataBuilder.BuildProduct(stockQuantity: 110);

        _productRepo
            .AdjustStockAtomicAsync(TestDataBuilder.ProductId1, 10, Arg.Any<CancellationToken>())
            .Returns(updatedProduct);

        await _sut.AdjustStockAsync(TestDataBuilder.ProductId1, 10, CancellationToken.None);

        await _auditRepo
            .Received(1)
            .LogAsync(
                Arg.Is<AuditLog>(l =>
                    l.Event       == "StockAdjusted"           &&
                    l.Actor       == "Warehouse"               &&
                    l.EntityId    == TestDataBuilder.ProductId1 &&
                    l.Delta       == 10                        &&
                    l.StockBefore == 100                       &&
                    l.StockAfter  == 110),
                Arg.Any<CancellationToken>());
    }
}
