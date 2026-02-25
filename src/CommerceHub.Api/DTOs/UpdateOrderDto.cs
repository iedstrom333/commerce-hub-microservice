using System.ComponentModel.DataAnnotations;
using CommerceHub.Api.Models;

namespace CommerceHub.Api.DTOs;

public class UpdateOrderDto
{
    [Required]
    public required string CustomerId { get; set; }

    [Required]
    public required List<CheckoutItemDto> Items { get; set; }

    [Required]
    [AllowedValues(
        OrderStatus.Pending, OrderStatus.Processing, OrderStatus.Shipped, OrderStatus.Cancelled,
        ErrorMessage = "Status must be one of: Pending, Processing, Shipped, Cancelled.")]
    public required string Status { get; set; }
}
