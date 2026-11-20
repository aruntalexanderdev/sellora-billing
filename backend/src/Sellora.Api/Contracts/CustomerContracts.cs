using System.ComponentModel.DataAnnotations;

namespace Sellora.Api.Contracts;

public record CustomerDto(Guid Id, string Name, string? Phone, string? Address, bool IsWalkIn, bool IsActive,
    Guid? CountryId = null, string? CountryName = null, Guid? StateId = null, string? StateName = null);

public record UpsertCustomerRequest(
    [Required, MaxLength(150)] string Name,
    [MaxLength(20)] string? Phone,
    [MaxLength(250)] string? Address,
    Guid? CountryId = null,
    Guid? StateId = null
);
