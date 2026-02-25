using CommerceHub.Api.DTOs;
using CommerceHub.Api.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CommerceHub.Api.Controllers;

[ApiController]
[Route("api/products")]
[Produces("application/json")]
public class ProductsController : ControllerBase
{
    private readonly IProductService _productService;

    public ProductsController(IProductService productService)
    {
        _productService = productService;
    }

    /// <summary>
    /// Returns all products, sorted by name.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<ProductResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var products = await _productService.GetAllAsync(ct);
        return Ok(products);
    }

    /// <summary>
    /// Atomically adjusts product stock. Prevents stock from going below zero.
    /// Use a positive delta to restock; use a negative delta to decrement.
    /// </summary>
    [HttpPatch("{id}/stock")]
    [ProducesResponseType(typeof(ProductStockResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AdjustStock(
        string id,
        [FromBody] StockAdjustmentDto dto,
        CancellationToken ct)
    {
        if (dto.Delta == 0)
            return UnprocessableEntity(new { message = "Delta cannot be zero." });

        var result = await _productService.AdjustStockAsync(id, dto.Delta, ct);

        if (result.IsFailure)
        {
            return result.Error == "NOT_FOUND"
                ? NotFound()
                : UnprocessableEntity(new { message = result.Error });
        }

        return Ok(result.Value);
    }
}
