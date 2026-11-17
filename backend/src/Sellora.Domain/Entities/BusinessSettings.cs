using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>Singleton row — one business identity per deployment, required for invoices (docx §8.5).</summary>
public class BusinessSettings : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }
    public required string BusinessName { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Gstin { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public required string InvoicePrefix { get; set; }

    /// <summary>The single currency all reports, stock valuation and sales are expressed in.</summary>
    /// <summary>FI-D39: how consignment receipts are posted. Direct (default) keeps Phase 05 behaviour.</summary>
    public ReceiptPostingMode ReceiptPostingMode { get; set; } = ReceiptPostingMode.Direct;

    /// <summary>FI-D39: when true the poster/approver of a receipt must differ from its recorder.</summary>
    public bool ReceiptSeparationOfDuties { get; set; }

    public Guid BaseCurrencyId { get; set; }
    public Currency BaseCurrency { get; set; } = null!;
}
