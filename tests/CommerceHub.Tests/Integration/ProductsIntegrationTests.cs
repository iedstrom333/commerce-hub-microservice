using System.Net;
using System.Net.Http.Json;
using CommerceHub.Api.DTOs;
using CommerceHub.Api.Models;
using FluentAssertions;
using MongoDB.Driver;
using NUnit.Framework;

namespace CommerceHub.Tests.Integration;

[TestFixture]
public class ProductsIntegrationTests : IntegrationTestBase
{
    // ----------------------------------------------------------------
    // TEST 1: GET /api/products returns all three seeded products
    // ----------------------------------------------------------------
    [Test]
    public async Task GetProducts_ReturnsAllThreeSeededProducts()
    {
        var response  = await Client.GetAsync("/api/products");
        var products  = await response.Content.ReadFromJsonAsync<List<ProductResponseDto>>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        products.Should().HaveCount(3);
        products!.Select(p => p.Name).Should().BeEquivalentTo(
            ["Gadget Basic", "Thingamajig Elite", "Widget Pro"]); // sorted by name
    }

    // ----------------------------------------------------------------
    // TEST 2: PATCH /api/products/{id}/stock with positive delta
    //         returns 200 and the stock is updated in the database
    // ----------------------------------------------------------------
    [Test]
    public async Task AdjustStock_WithPositiveDelta_Returns200AndUpdatesDatabase()
    {
        var response = await Client.PatchAsJsonAsync(
            "/api/products/000000000000000000000001/stock",
            new StockAdjustmentDto { Delta = 10 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var product = await Db.GetCollection<Product>("Products")
            .Find(Builders<Product>.Filter.Eq(p => p.Id, "000000000000000000000001"))
            .FirstOrDefaultAsync();

        product!.StockQuantity.Should().Be(110); // 100 + 10
    }

    // ----------------------------------------------------------------
    // TEST 3: Successful stock adjustment writes a StockAdjusted audit entry
    //         — regression test for the CancellationToken fire-and-forget bug
    // ----------------------------------------------------------------
    [Test]
    public async Task AdjustStock_WhenSuccessful_WritesStockAdjustedAuditLog()
    {
        await Client.PatchAsJsonAsync(
            "/api/products/000000000000000000000002/stock",
            new StockAdjustmentDto { Delta = 5 });

        var log = await WaitForAuditLogAsync("StockAdjusted", "000000000000000000000002");

        log.Should().NotBeNull("StockAdjusted audit entry must be written after PATCH /stock");
        log!.Actor.Should().Be("Warehouse");
        log.Delta.Should().Be(5);
        log.StockBefore.Should().Be(50);
        log.StockAfter.Should().Be(55);
    }

    // ----------------------------------------------------------------
    // TEST 4: PATCH with delta = 0 returns 422 before touching the database
    // ----------------------------------------------------------------
    [Test]
    public async Task AdjustStock_WithZeroDelta_Returns422()
    {
        var response = await Client.PatchAsJsonAsync(
            "/api/products/000000000000000000000001/stock",
            new StockAdjustmentDto { Delta = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ----------------------------------------------------------------
    // TEST 5: Negative delta that would make stock go below zero returns 422
    // ----------------------------------------------------------------
    [Test]
    public async Task AdjustStock_WhenWouldCauseNegativeStock_Returns422()
    {
        var response = await Client.PatchAsJsonAsync(
            "/api/products/000000000000000000000003/stock",
            new StockAdjustmentDto { Delta = -10 }); // Thingamajig Elite has only 5

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ----------------------------------------------------------------
    // TEST 6: PATCH for a nonexistent product returns 404
    // ----------------------------------------------------------------
    [Test]
    public async Task AdjustStock_WhenProductNotFound_Returns404()
    {
        var response = await Client.PatchAsJsonAsync(
            "/api/products/000000000000000000000099/stock",
            new StockAdjustmentDto { Delta = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----------------------------------------------------------------
    // TEST 7: GET /health returns 200 with "Healthy" when MongoDB is reachable
    // ----------------------------------------------------------------
    [Test]
    public async Task HealthCheck_WhenMongoIsReachable_Returns200Healthy()
    {
        var response = await Client.GetAsync("/health");
        var body     = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().ContainEquivalentOf("Healthy");
    }
}
