namespace Sellora.Domain.Entities;

public class Tax
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public decimal RatePercent { get; set; }
    public bool IsActive { get; set; } = true;
}
