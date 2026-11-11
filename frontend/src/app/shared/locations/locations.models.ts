/** Mirrors Domain/Inventory/StockLocationType.cs (string enum). */
export type StockLocationType = 'BranchStore' | 'BranchWarehouse' | 'CompanyWarehouse' | 'Vehicle' | 'Transit';

/** Mirrors Api/Contracts/StockLocationContracts.cs StockLocationDto. */
export interface StockLocation {
  id: string;
  code: string;
  name: string;
  locationType: StockLocationType;
  branchId: string | null;
  branchCode: string | null;
  branchName: string | null;
  isActive: boolean;
  canSell: boolean;
  canReceivePurchase: boolean;
  isSystemManaged: boolean;
}

export type LocationPurpose = 'sale' | 'purchase' | 'adjust' | 'transfer' | 'filter';

export interface StockLocationQuery {
  type?: StockLocationType;
  branchId?: string;
  includeInactive?: boolean;
}

/** Eligibility per D10: sale -> CanSell, purchase -> CanReceivePurchase, adjust/transfer/filter -> any non-Transit location. */
export function isEligibleLocation(location: StockLocation, purpose: LocationPurpose): boolean {
  switch (purpose) {
    case 'sale':
      return location.canSell;
    case 'purchase':
      return location.canReceivePurchase;
    default:
      return location.locationType !== 'Transit';
  }
}
