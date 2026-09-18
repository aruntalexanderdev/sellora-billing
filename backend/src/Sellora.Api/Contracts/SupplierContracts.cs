using System.ComponentModel.DataAnnotations;
using Sellora.Domain.Entities;

namespace Sellora.Api.Contracts;

public record SupplierDto(
    Guid Id, string Name, string? City, string? Gstin, string? PaymentTerms, bool IsActive,
    Guid CurrencyId, string CurrencyCode, string CurrencySymbol, int CurrencyDecimalPrecision
);

public record UpsertSupplierRequest(
    [Required, MaxLength(150)] string Name,
    [MaxLength(80)] string? City,
    [MaxLength(20)] string? Gstin,
    [MaxLength(40)] string? PaymentTerms,
    [Required] Guid? CurrencyId
);

public static class SupplierMapping
{
    public static SupplierDto ToDto(Supplier s) => new(s.Id, s.Name, s.City, s.Gstin, s.PaymentTerms, s.IsActive, s.CurrencyId, s.Currency.Code, s.Currency.Symbol, s.Currency.DecimalPrecision);
}
