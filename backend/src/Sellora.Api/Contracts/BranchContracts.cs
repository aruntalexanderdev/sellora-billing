using System.ComponentModel.DataAnnotations;

namespace Sellora.Api.Contracts;

/// <summary><see cref="EffectiveInvoicePrefix"/>: the prefix this branch's invoices carry today (company prefix for the default
/// branch; own prefix, else company prefix + branch code, for the others).</summary>
public record BranchDto(
    Guid Id, string Code, string Name, string? AddressLine, string? City, string? State, string? Phone, string? Email,
    string? Gstin, string? InvoicePrefix, string EffectiveInvoicePrefix, bool IsDefault, bool IsActive
);

/// <summary>A branch the caller may work in (the source of a branch picker).</summary>
public record AccessibleBranchDto(Guid Id, string Code, string Name, bool IsDefault);

public record CreateBranchRequest(
    [Required, MaxLength(6)] string Code,
    [Required, MaxLength(150)] string Name,
    [MaxLength(300)] string? AddressLine,
    [MaxLength(80)] string? City,
    [MaxLength(80)] string? State,
    [MaxLength(30)] string? Phone,
    [EmailAddress, MaxLength(200)] string? Email,
    [MaxLength(20)] string? Gstin,
    [MaxLength(10)] string? InvoicePrefix
);

/// <summary>The code and the default flag cannot be edited.</summary>
public record UpdateBranchRequest(
    [Required, MaxLength(150)] string Name,
    [MaxLength(300)] string? AddressLine,
    [MaxLength(80)] string? City,
    [MaxLength(80)] string? State,
    [MaxLength(30)] string? Phone,
    [EmailAddress, MaxLength(200)] string? Email,
    [MaxLength(20)] string? Gstin,
    [MaxLength(10)] string? InvoicePrefix
);
