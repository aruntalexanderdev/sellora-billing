using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

public record SupplierDto(Guid Id, string Name, string? City, string? Gstin, string? PaymentTerms, bool IsActive);

public record UpsertSupplierRequest(
    [Required, MaxLength(150)] string Name,
    [MaxLength(80)] string? City,
    [MaxLength(20)] string? Gstin,
    [MaxLength(40)] string? PaymentTerms
);

public static class SupplierMapping
{
    public static SupplierDto ToDto(Supplier s) => new(s.Id, s.Name, s.City, s.Gstin, s.PaymentTerms, s.IsActive);
}
