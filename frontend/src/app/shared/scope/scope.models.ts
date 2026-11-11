/** Branch/location fields shared by document DTOs (camelCase mirrors of the backend `Branch*` / `StockLocation*` record members). */
export interface BranchFields {
  branchId: string;
  branchCode: string | null;
  branchName: string | null;
}

/** Expenses may be company-level (no branch). */
export interface OptionalBranchFields {
  branchId: string | null;
  branchCode: string | null;
  branchName: string | null;
}

export interface LocationFields {
  stockLocationId: string | null;
  stockLocationCode: string | null;
  stockLocationName: string | null;
}
