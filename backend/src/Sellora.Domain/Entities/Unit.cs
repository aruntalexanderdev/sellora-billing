namespace Sellora.Domain.Entities;

public class Unit
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string ShortCode { get; set; }
    public UnitType Type { get; set; }
    public bool IsActive { get; set; } = true;
}
