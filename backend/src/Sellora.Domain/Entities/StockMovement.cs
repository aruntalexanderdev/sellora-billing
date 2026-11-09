using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>The unified ledger — every increase and decrease to a product's stock, with its source document.</summary>
public class StockMovement : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public StockMovementType Type { get; set; }

    /// <summary>Signed change in base units — positive for increases, negative for decreases.</summary>
    public decimal QuantityBaseUnits { get; set; }

    /// <summary>The batch this movement created or removed stock from, when it touches exactly one. Null for
    /// movements that can span several batches (sales, adjustment decreases) — those are traced through
    /// <see cref="SalesInvoiceLineBatchConsumption"/> — and for movements recorded before batch tracking.</summary>
    public Guid? PurchaseBatchId { get; set; }
    public PurchaseBatch? PurchaseBatch { get; set; }

    public required string ReferenceType { get; set; }
    public Guid ReferenceId { get; set; }
    public required string ReferenceNo { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Notes { get; set; }

    /// <summary>Why the stock changed, for movements that came from a stock adjustment (the adjustment's type; also
    /// <see cref="AdjustmentType.OpeningStock"/> on <see cref="StockMovementType.OpeningStock"/> rows). Null for movements
    /// from other documents.</summary>
    public AdjustmentType? ReasonCode { get; set; }

    /// <summary>Quantity as entered on the document line, in <see cref="EnteredProductUnitId"/>'s unit. Written only when
    /// this one row carries the whole document line, so EnteredQuantity x <see cref="ConversionFactorSnapshot"/> =
    /// |<see cref="QuantityBaseUnits"/>| (DB CHECK). Null on rows that split a line (adjustment decrease across several
    /// batches) and on legacy rows where the source line could not be identified unambiguously.</summary>
    public decimal? EnteredQuantity { get; set; }

    /// <summary>The product unit the document line was entered in. Written on every new movement (Phase 6a); null on
    /// unmatched legacy rows. FK is (ProductId, EnteredProductUnitId) -> ProductUnits (ProductId, Id).</summary>
    public Guid? EnteredProductUnitId { get; set; }
    public ProductUnit? EnteredProductUnit { get; set; }

    /// <summary>Snapshot of the entered unit's short code. All-or-none with <see cref="EnteredProductUnitId"/>.</summary>
    public string? EnteredUnitCode { get; set; }

    /// <summary>Snapshot of the entered unit's conversion factor to base. All-or-none with <see cref="EnteredProductUnitId"/>.</summary>
    public decimal? ConversionFactorSnapshot { get; set; }
}
