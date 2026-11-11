/** Mirrors Api/Contracts/WarehouseContracts.cs WarehouseDto. */
export interface Warehouse {
  id: string;
  stockLocationId: string;
  code: string;
  name: string;
  ownership: 'Company' | 'Branch';
  branchId: string | null;
  branchCode: string | null;
  branchName: string | null;
  isActive: boolean;
  createdAt: string;
}

/** Mirrors CreateWarehouseRequest; a null branchId creates a company-level warehouse. */
export interface CreateWarehouseRequest {
  code: string;
  name: string;
  branchId: string | null;
}

/** Mirrors UpdateWarehouseRequest (code, branch and ownership cannot be edited). */
export interface UpdateWarehouseRequest {
  name: string;
}

export interface WarehouseQuery {
  branchId?: string | null;
  ownership?: 'Company' | 'Branch' | '';
  includeInactive?: boolean;
}
