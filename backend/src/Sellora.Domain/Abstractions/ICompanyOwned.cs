namespace Sellora.Domain.Abstractions;

/// <summary>
/// Marks an entity that belongs to exactly one company (tenant). The column is stamped on insert from the current
/// <see cref="ICompanyContext"/> and filtered on every read by the "Company" named query filter.
/// </summary>
public interface ICompanyOwned
{
    Guid CompanyId { get; set; }
}
