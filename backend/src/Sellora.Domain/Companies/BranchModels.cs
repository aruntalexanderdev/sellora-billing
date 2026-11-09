namespace Sellora.Domain.Companies;

/// <summary>Input for a new branch. <see cref="Code"/> is trimmed and upper-cased by the service; it is immutable afterwards.</summary>
public sealed record CreateBranchInput(
    string Code,
    string Name,
    string? AddressLine,
    string? City,
    string? State,
    string? Phone,
    string? Email,
    string? Gstin,
    string? InvoicePrefix
);

/// <summary>Editable branch fields (the code and the default flag cannot change).</summary>
public sealed record UpdateBranchInput(
    string Name,
    string? AddressLine,
    string? City,
    string? State,
    string? Phone,
    string? Email,
    string? Gstin,
    string? InvoicePrefix
);
