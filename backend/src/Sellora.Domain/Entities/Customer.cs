using Sellora.Domain.Abstractions;

namespace Sellora.Domain.Entities;

/// <summary>
/// A sales customer, including the seeded "Walk-in Customer" row used for anonymous POS sales
/// (docx §11.1). <see cref="IsWalkIn"/> customers cannot be deactivated — POS always needs one.
/// </summary>
public class Customer : ICompanyOwned
{
    public Guid CompanyId { get; set; }

    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public bool IsWalkIn { get; set; }
    public bool IsActive { get; set; } = true;
}
