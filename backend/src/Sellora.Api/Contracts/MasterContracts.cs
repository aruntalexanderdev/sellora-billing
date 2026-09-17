using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

public record UnitDto(Guid Id, string Name, string ShortCode, UnitType Type, bool IsActive);

public record UpsertUnitRequest(
    [Required, MaxLength(60)] string Name,
    [Required, MaxLength(10)] string ShortCode,
    UnitType Type
);

public record TaxDto(Guid Id, string Name, decimal RatePercent, bool IsActive);

public record UpsertTaxRequest(
    [Required, MaxLength(60)] string Name,
    [Range(0, 100)] decimal RatePercent
);

public record BusinessSettingsDto(
    Guid Id, string BusinessName, string? AddressLine, string? City, string? State,
    string? Gstin, string? Phone, string? Email, string InvoicePrefix
);

public record UpdateBusinessSettingsRequest(
    [Required, MaxLength(150)] string BusinessName,
    [MaxLength(300)] string? AddressLine,
    [MaxLength(80)] string? City,
    [MaxLength(80)] string? State,
    [MaxLength(20)] string? Gstin,
    [MaxLength(30)] string? Phone,
    [EmailAddress, MaxLength(200)] string? Email,
    [Required, MaxLength(10)] string InvoicePrefix
);

public record SetActiveRequest(bool IsActive);
