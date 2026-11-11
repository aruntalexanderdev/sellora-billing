using Sellora.Domain.Companies;
using Sellora.Domain.Entities;

namespace Sellora.Domain.Abstractions;

/// <summary>Branch writes of the current company (Phase 2). Every method that can break numbering rules (code, prefix)
/// runs under a row lock on the company so concurrent branch edits and settings changes cannot both pass validation.</summary>
public interface IBranchService
{
    /// <summary>Creates an active, non-default branch. Throws <see cref="DomainValidationException"/> (fields <c>Code</c>,
    /// <c>Name</c>, <c>Gstin</c>, <c>InvoicePrefix</c>) for a bad or duplicate code/name, an invalid GSTIN or an invoice prefix
    /// that would collide with the company prefix, another branch's prefix or existing invoice numbers, or numbers longer than 16 characters.</summary>
    Task<Branch> CreateAsync(CreateBranchInput input, Guid userId, CancellationToken ct = default);

    /// <summary>Edits a branch. Returns null if it does not exist in the company. The code cannot change.</summary>
    Task<Branch?> UpdateAsync(Guid branchId, UpdateBranchInput input, Guid userId, CancellationToken ct = default);

    /// <summary>Activates or deactivates a branch. The default branch cannot be deactivated (<c>IsActive</c>). Requires a reason (1-500 chars, field <c>reason</c>), recorded in the audit event. Returns null if not found.</summary>
    Task<Branch?> SetStatusAsync(Guid branchId, bool isActive, string? reason, Guid userId, CancellationToken ct = default);

    /// <summary>Settings PUT hook: locks the company row and rejects (<c>InvoicePrefix</c>) a company prefix equal to a branch's own
    /// prefix or that would make a branch number exceed 16 characters. Call inside the transaction that saves the settings.</summary>
    Task EnsureCompanyInvoicePrefixAllowedAsync(string companyInvoicePrefix, CancellationToken ct = default);
}
