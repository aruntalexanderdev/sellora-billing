export interface BusinessSettings {
  id: string;
  businessName: string;
  addressLine: string | null;
  city: string | null;
  state: string | null;
  gstin: string | null;
  phone: string | null;
  email: string | null;
  invoicePrefix: string;
}

export interface UpdateBusinessSettingsRequest {
  businessName: string;
  addressLine?: string | null;
  city?: string | null;
  state?: string | null;
  gstin?: string | null;
  phone?: string | null;
  email?: string | null;
  invoicePrefix: string;
}
