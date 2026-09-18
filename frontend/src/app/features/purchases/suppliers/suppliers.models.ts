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
}

export interface UpsertSupplierRequest {
  name: string;
  city?: string | null;
  gstin?: string | null;
  paymentTerms?: string | null;
  currencyId: string;
}
