export interface SupplierItem {
  id: string;
  name: string;
  city: string | null;
  gstin: string | null;
  paymentTerms: string | null;
  isActive: boolean;
}

export interface UpsertSupplierRequest {
  name: string;
  city?: string | null;
  gstin?: string | null;
  paymentTerms?: string | null;
}
