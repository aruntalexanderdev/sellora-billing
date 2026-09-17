namespace Sellora.Domain.Entities;

public class Product
{
    public Guid Id { get; set; }
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public string? Category { get; set; }

    public Guid BaseUnitId { get; set; }
    public Unit BaseUnit { get; set; } = null!;

    public Guid TaxId { get; set; }
    public Tax Tax { get; set; } = null!;

    /// <summary>Reorder threshold in the base unit — drives the low-stock badge once inventory exists.</summary>
    public decimal ReorderLevel { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<ProductSellingMode> SellingModes { get; set; } = new List<ProductSellingMode>();
}
