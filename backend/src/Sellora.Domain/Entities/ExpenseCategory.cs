using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>A grouping for operating expenses (Rent, Electricity, Salary…). Categories are deactivated, never
/// deleted, so historical expenses keep their category.</summary>
public class ExpenseCategory : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
}
