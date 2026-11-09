using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// A customer returning goods from a sales invoice. Completing one puts the goods back into the exact
/// purchase batches they were sold from, and credits the invoice: its net total falls by the value of the
/// returned goods (tax included), so any amount already paid above that becomes refundable.
/// A return is permanent — mistakes are corrected with a new document, never by editing history.
/// </summary>
public class SalesReturn : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    /// <summary>The branch that owns this document (Phase 2, same-company composite FK). Never taken from a client body.</summary>
    public Guid BranchId { get; set; }

    public Guid Id { get; set; }
    public required string ReturnNo { get; set; }

    public Guid SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice { get; set; } = null!;

    public DateOnly ReturnDate { get; set; }
    public string? Reason { get; set; }

    public decimal TaxableTotal { get; set; }
    public decimal TaxTotal { get; set; }

    /// <summary>The credit given to the customer: <see cref="TaxableTotal"/> + <see cref="TaxTotal"/>.</summary>
    public decimal Total { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<SalesReturnLine> Lines { get; set; } = new List<SalesReturnLine>();
}

public class SalesReturnLine
{
    public Guid Id { get; set; }

    public Guid SalesReturnId { get; set; }
    public SalesReturn SalesReturn { get; set; } = null!;

    public Guid SalesInvoiceLineId { get; set; }
    public SalesInvoiceLine SalesInvoiceLine { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The unit this return line was entered in (may differ from the sold line's unit). FK is (ProductId, ProductUnitId) -> ProductUnits (ProductId, Id).</summary>
    public Guid ProductUnitId { get; set; }
    public ProductUnit ProductUnit { get; set; } = null!;

    /// <summary>Snapshot of <see cref="ProductUnit.UnitId"/> at return time.</summary>
    public Guid UnitId { get; set; }
    public Unit Unit { get; set; } = null!;

    /// <summary>Snapshot of <c>Unit.ShortCode</c> at return time.</summary>
    public string UnitCode { get; set; } = string.Empty;

    /// <summary>Snapshot of the return unit's conversion factor.</summary>
    public decimal ConversionFactorToBase { get; set; }

    /// <summary>Quantity returned, entered in this line's own unit (<see cref="ProductUnitId"/>).</summary>
    public decimal Qty { get; set; }
    public decimal BaseQty { get; set; }

    public decimal TaxableValue { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }

    /// <summary>The landed cost of the batches the goods went back into — what comes off cost of goods sold.</summary>
    public decimal CostRestored { get; set; }
}
