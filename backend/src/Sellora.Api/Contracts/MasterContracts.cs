using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

/// <summary><see cref="IsInUse"/>: a product uses the unit as base unit or any product unit row (active or inactive) uses
/// it; category, decimal places and standard factor are then frozen. <see cref="CanDeactivate"/>: no product uses it as
/// base unit and no <em>active</em> product unit row uses it.</summary>
public record UnitDto(Guid Id, string Name, string ShortCode, MeasurementCategory MeasurementCategory, short DecimalPlaces, decimal? StandardFactor, bool IsActive, bool IsInUse, bool CanDeactivate);

public record UpsertUnitRequest(
    [Required, MaxLength(60)] string Name,
    [Required, MaxLength(10)] string ShortCode,
    [Required] MeasurementCategory? MeasurementCategory,
    [Range(0, 3)] short DecimalPlaces,
    [Range(typeof(decimal), "0.0000000001", "1000000000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] decimal? StandardFactor
);

public record TaxDto(Guid Id, string Name, decimal RatePercent, bool IsActive);

public record UpsertTaxRequest(
    [Required, MaxLength(60)] string Name,
    [Range(0, 100)] decimal RatePercent
);

public record BusinessSettingsDto(
    Guid Id, string BusinessName, string? AddressLine, string? City, string? State,
    string? Gstin, string? Phone, string? Email, string InvoicePrefix,
    Guid BaseCurrencyId, string BaseCurrencyCode, string BaseCurrencySymbol, Guid CompanyId
);

public record UpdateBusinessSettingsRequest(
    [Required, MaxLength(150)] string BusinessName,
    [MaxLength(300)] string? AddressLine,
    [MaxLength(80)] string? City,
    [MaxLength(80)] string? State,
    [MaxLength(20)] string? Gstin,
    [MaxLength(30)] string? Phone,
    [EmailAddress, MaxLength(200)] string? Email,
    [Required, MaxLength(10)] string InvoicePrefix,
    [Required] Guid? BaseCurrencyId
);

public record SetActiveRequest(bool IsActive);

/// <summary>Status change of a branch / warehouse / vehicle (Phase 11a D14). <c>Reason</c> is required (trimmed, 1-500 chars); validated by the service so the 400 names field <c>reason</c>.</summary>
public record SetStatusRequest(bool IsActive, string? Reason);
