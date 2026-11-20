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
  /** Country/State links; `state` above is the read-only legacy hint, shown only while stateId is null (D6). */
  countryId: string | null;
  countryName: string | null;
  stateId: string | null;
  stateName: string | null;
}

/** Mirrors UpdateBranchRequest (the code and default flag cannot be edited). */
export interface UpdateBranchRequest {
  name: string;
  addressLine: string | null;
  city: string | null;
  phone: string | null;
  email: string | null;
  gstin: string | null;
  invoicePrefix: string | null;
  countryId: string | null;
  stateId: string | null;
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
