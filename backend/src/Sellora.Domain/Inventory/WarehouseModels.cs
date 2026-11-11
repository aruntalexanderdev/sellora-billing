namespace Sellora.Domain.Inventory;

/// <summary>Create input. A null <paramref name="BranchId"/> creates a CompanyWarehouse, otherwise a BranchWarehouse.</summary>
public sealed record CreateWarehouseInput(string Code, string Name, Guid? BranchId);

/// <summary>Only the name is editable; code, branch and ownership kind are immutable.</summary>
public sealed record UpdateWarehouseInput(string Name);
