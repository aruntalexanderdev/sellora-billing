namespace Sellora.Domain.Entities;

public class Supplier
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? City { get; set; }
    public string? Gstin { get; set; }
    public string? PaymentTerms { get; set; }
    public bool IsActive { get; set; } = true;
}
