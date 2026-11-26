namespace Sellora.Domain.Inventory;

/// <summary>Create/update input for a category or subcategory master (Phase 08, FI-D16). Code is trimmed and upper-cased by the service. <c>HsnCode</c> (4, 6 or 8 digits, empty = none) is stored on subcategories only (BR9) and ignored for categories.</summary>
public sealed record UpsertProductClassificationInput(string Code, string Name, int SortOrder, string? HsnCode = null);
