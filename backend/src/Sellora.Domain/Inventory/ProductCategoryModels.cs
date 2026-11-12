namespace Sellora.Domain.Inventory;

/// <summary>Create/update input for a category or subcategory master (Phase 08, FI-D16). Code is trimmed and upper-cased by the service.</summary>
public sealed record UpsertProductClassificationInput(string Code, string Name, int SortOrder);
