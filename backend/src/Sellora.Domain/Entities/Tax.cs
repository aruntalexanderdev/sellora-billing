using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

public class Tax : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }
    public required string Name { get; set; }
    public decimal RatePercent { get; set; }
    public bool IsActive { get; set; } = true;
}
