namespace Sellora.Domain.Entities;

/// <summary>Singleton row — one business identity per deployment, required for invoices (docx §8.5).</summary>
public class BusinessSettings
{
    public Guid Id { get; set; }
    public required string BusinessName { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Gstin { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public required string InvoicePrefix { get; set; }
}
