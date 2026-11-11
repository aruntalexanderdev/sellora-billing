/** Mirrors Api/Contracts/BranchContracts.cs BranchDto. */
export interface Branch {
  id: string;
  code: string;
  name: string;
  addressLine: string | null;
  city: string | null;
  state: string | null;
  phone: string | null;
  email: string | null;
  gstin: string | null;
  invoicePrefix: string | null;
  effectiveInvoicePrefix: string;
  isDefault: boolean;
  isActive: boolean;
}

/** Mirrors UpdateBranchRequest (the code and default flag cannot be edited). */
export interface UpdateBranchRequest {
  name: string;
  addressLine: string | null;
  city: string | null;
  state: string | null;
  phone: string | null;
  email: string | null;
  gstin: string | null;
  invoicePrefix: string | null;
}

/** Mirrors CreateBranchRequest. */
export interface CreateBranchRequest extends UpdateBranchRequest {
  code: string;
}

/** Mirrors Api/Contracts/BranchContracts.cs AccessibleBranchDto. */
export interface AccessibleBranchOption {
  id: string;
  code: string;
  name: string;
  isDefault: boolean;
}
