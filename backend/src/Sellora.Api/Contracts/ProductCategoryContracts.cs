using System.ComponentModel.DataAnnotations;

namespace Sellora.Api.Contracts;

/// <summary><see cref="ProductCount"/> = products referencing the subcategory, any status.</summary>
public record ProductSubcategoryDto(Guid Id, Guid CategoryId, string Code, string Name, int SortOrder, bool IsActive, int ProductCount);

/// <summary><see cref="Subcategories"/> ordered SortOrder, Name; filtered by includeInactive like the category itself.</summary>
public record ProductCategoryDto(
    Guid Id, string Code, string Name, int SortOrder, bool IsActive, int ProductCount, IReadOnlyList<ProductSubcategoryDto> Subcategories);

/// <summary>Code is trimmed and upper-cased by the service; format and length rules are business rules (400 on <c>code</c>).</summary>
public record UpsertProductClassificationRequest(string? Code, string? Name, int SortOrder);
