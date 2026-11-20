export interface SupplierItem {
  id: string;
  name: string;
  city: string | null;
  gstin: string | null;
  paymentTerms: string | null;
  isActive: boolean;
  currencyId: string;
  currencyCode: string;
  currencySymbol: string;
  currencyDecimalPrecision: number;
  countryId: string | null;
  countryName: string | null;
  stateId: string | null;
  stateName: string | null;
}

export interface UpsertSupplierRequest {
  name: string;
  city?: string | null;
  gstin?: string | null;
  paymentTerms?: string | null;
  currencyId: string;
  countryId: string | null;
  stateId: string | null;
}
