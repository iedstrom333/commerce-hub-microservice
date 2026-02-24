using System.ComponentModel.DataAnnotations;

namespace CommerceHub.Api.DTOs;

public class UpdateOrderDto
{
    [Required]
    public required string CustomerId { get; set; }

    [Required]
    public required List<CheckoutItemDto> Items { get; set; }

    [Required]
    public required string Status { get; set; }
}
