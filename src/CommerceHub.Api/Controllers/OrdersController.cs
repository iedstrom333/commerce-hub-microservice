using CommerceHub.Api.DTOs;
using CommerceHub.Api.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CommerceHub.Api.Controllers;

[ApiController]
[Route("api/orders")]
[Produces("application/json")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;

    public OrdersController(IOrderService orderService)
    {
        _orderService = orderService;
    }

    /// <summary>
    /// Processes a new order. Verifies stock, atomically decrements inventory,
    /// creates the order, and publishes an OrderCreated event.
    /// </summary>
    [HttpPost("checkout")]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Checkout(
        [FromBody] CheckoutRequestDto dto,
        CancellationToken ct)
    {
        var result = await _orderService.CheckoutAsync(dto, ct);

        if (result.IsFailure)
            return UnprocessableEntity(new { message = result.Error });

        return CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value);
    }

    /// <summary>
    /// Retrieves a specific order by its unique ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string id, CancellationToken ct)
    {
        var order = await _orderService.GetByIdAsync(id, ct);
        return order is null ? NotFound() : Ok(order);
    }

    /// <summary>
    /// Idempotent full replacement of an existing order.
    /// Updates are blocked if the order status is already Shipped.
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] UpdateOrderDto dto,
        CancellationToken ct)
    {
        var result = await _orderService.UpdateAsync(id, dto, ct);

        if (result.IsFailure)
        {
            return result.Error == "NOT_FOUND"
                ? NotFound()
                : Conflict(new { message = result.Error });
        }

        return Ok(result.Value);
    }
}
