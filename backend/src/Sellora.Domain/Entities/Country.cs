namespace Sellora.Domain.Entities;

/// <summary>Global country master (no CompanyId, like <see cref="Currency"/>). Never deleted; deactivate instead.</summary>
public class Country
{
    public Guid Id { get; set; }

    /// <summary>ISO 3166-1 alpha-2, upper case, unique.</summary>
    public required string Code { get; set; }
    /// <summary>Optional ISO 3166-1 alpha-3.</summary>
    public string? Iso3Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Seeded row: ISO identity (Code) is locked.</summary>
    public bool IsSystem { get; set; }
}
