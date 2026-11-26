using System.ComponentModel.DataAnnotations;

namespace Sellora.Api.Contracts;

/// <summary><see cref="ProductCount"/> = products referencing the subcategory, any status. <see cref="HsnCode"/>: the default HSN/SAC code of its products (BR9).</summary>
public record ProductSubcategoryDto(Guid Id, Guid CategoryId, string Code, string Name, int SortOrder, bool IsActive, int ProductCount, string? HsnCode = null,
    // TP-5a (T11): set on the subcategory update response only: count of GST-taxable products left without an HSN (field hsnCode). The save is never blocked.
    IReadOnlyList<FieldErrorDto>? Warnings = null);

/// <summary><see cref="Subcategories"/> ordered SortOrder, Name; filtered by includeInactive like the category itself.</summary>
public record ProductCategoryDto(
    Guid Id, string Code, string Name, int SortOrder, bool IsActive, int ProductCount, IReadOnlyList<ProductSubcategoryDto> Subcategories);

/// <summary>Code is trimmed and upper-cased by the service; format and length rules are business rules (400 on <c>code</c>). <c>HsnCode</c> (4, 6 or 8 digits, empty = none; 400 on <c>hsnCode</c>) applies to subcategories only.</summary>
public record UpsertProductClassificationRequest(string? Code, string? Name, int SortOrder, [MaxLength(8)] string? HsnCode = null);
