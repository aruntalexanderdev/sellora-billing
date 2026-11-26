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
  /** `state` above is the read-only legacy hint, shown only while stateId is null (D6). */
  countryId: string | null;
  countryName: string | null;
  stateId: string | null;
  stateName: string | null;
  /** Whether stored selling prices include tax (T2/T18). */
  sellingPricesIncludeTax: boolean;
  /** Cap of a whole-bill discount in percent; null = no cap (T15). */
  maxBillDiscountPercent: number | null;
}

export interface UpdateBusinessSettingsRequest {
  businessName: string;
  addressLine?: string | null;
  city?: string | null;
  gstin?: string | null;
  phone?: string | null;
  email?: string | null;
  invoicePrefix: string;
  baseCurrencyId: string;
  /** Omitted = unchanged. Applies only to receipts recorded afterwards. */
  receiptPostingMode?: ReceiptPostingMode;
  receiptSeparationOfDuties?: boolean;
  /** Always post the current values: null clears the link (D17/D22). */
  countryId: string | null;
  stateId: string | null;
  sellingPricesIncludeTax: boolean;
  /** 0-100; always post the current value, null clears the cap. */
  maxBillDiscountPercent: number | null;
}
