using System.ComponentModel.DataAnnotations;

namespace CommerceHub.Api.DTOs;

public class CheckoutRequestDto
{
    [Required]
    [MaxLength(50)]
    public required string CustomerId { get; set; }

    [Required]
    [MinLength(1, ErrorMessage = "At least one item is required.")]
    public required List<CheckoutItemDto> Items { get; set; }
}

public class CheckoutItemDto
{
    [Required]
    [MaxLength(24, ErrorMessage = "ProductId must be a 24-character ObjectId.")]
    public required string ProductId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Quantity must be at least 1.")]
    public int Quantity { get; set; }
}
