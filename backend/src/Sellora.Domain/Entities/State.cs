namespace Sellora.Domain.Entities;

/// <summary>Global first-level subdivision of a <see cref="Country"/> (state, union territory, emirate, region, governorate).
/// <see cref="CountryId"/> is immutable (part of the alternate key (CountryId, Id) the address FKs target).</summary>
public class State
{
    public Guid Id { get; set; }
    public Guid CountryId { get; set; }

    /// <summary>ISO 3166-2 suffix (1-3 chars of A-Z0-9), text because some codes are numeric with leading zeros.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public StateKind Kind { get; set; }

    /// <summary>2-digit GST state code; India only, unique when present.</summary>
    public string? GstStateCode { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSystem { get; set; }
}
