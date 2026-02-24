using System.ComponentModel.DataAnnotations;

namespace CommerceHub.Api.DTOs;

public class CheckoutRequestDto
{
    [Required]
    public required string CustomerId { get; set; }

    [Required]
    [MinLength(1, ErrorMessage = "At least one item is required.")]
    public required List<CheckoutItemDto> Items { get; set; }
}

public class CheckoutItemDto
{
    [Required]
    public required string ProductId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Quantity must be at least 1.")]
    public int Quantity { get; set; }
}
