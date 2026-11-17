/** Mirrors Domain ReceiptPostingMode (string enum). */
export type ReceiptPostingMode = 'Direct' | 'SupervisorApproval';

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
  baseCurrencyId: string;
  baseCurrencyCode: string;
  baseCurrencySymbol: string;
  companyId: string;
  receiptPostingMode: ReceiptPostingMode;
  receiptSeparationOfDuties: boolean;
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
  baseCurrencyId: string;
  /** Omitted = unchanged. Applies only to receipts recorded afterwards. */
  receiptPostingMode?: ReceiptPostingMode;
  receiptSeparationOfDuties?: boolean;
}
