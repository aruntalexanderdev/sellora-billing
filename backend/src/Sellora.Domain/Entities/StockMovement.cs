namespace Sellora.Domain.Entities;

/// <summary>The unified ledger — every increase and decrease to a product's stock, with its source document.</summary>
public class StockMovement
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public StockMovementType Type { get; set; }

    /// <summary>Signed change in base units — positive for increases, negative for decreases.</summary>
    public decimal QuantityBaseUnits { get; set; }

    public required string ReferenceType { get; set; }
    public Guid ReferenceId { get; set; }
    public required string ReferenceNo { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Notes { get; set; }
}
