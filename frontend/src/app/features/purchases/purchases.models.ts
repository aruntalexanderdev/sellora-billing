export type PurchaseStatus = 'Completed' | 'Cancelled';
export type PaymentStatus = 'Due' | 'PartiallyPaid' | 'Paid';
export type PaymentMethod = 'Cash' | 'Upi' | 'Card' | 'Credit' | 'BankTransfer';
export type PaymentEntryKind = 'Payment' | 'Refund' | 'Reversal';

export interface PurchaseListItem {
  id: string;
  purchaseNo: string;
  supplierName: string;
  purchaseDate: string;
  grandTotal: number;
  paymentStatus: PaymentStatus;
  status: PurchaseStatus;
  currencyCode: string;
  originalGrandTotal: number;
}

export interface PurchaseLine {
  id: string;
  productId: string;
  productName: string;
  productUnitId: string;
  unitLabel: string;
  qty: number;
  baseQty: number;
  baseUnitLabel: string;
  rate: number;
  taxRatePercent: number;
  otherCharges: number;
  discount: number;
  taxableValue: number;
  taxAmount: number;
  lineTotal: number;
  unitCost: number;
  returnedQty: number;
  returnableQty: number;
  internalBatchNo: string | null;
  supplierBatchNumber: string | null;
  receivedDate: string | null;
}

export interface PurchasePayment {
  id: string;
  kind: PaymentEntryKind;
  paymentDate: string;
  amount: number;
  exchangeRateToBase: number;
  baseAmount: number;
  paymentMethod: PaymentMethod;
  createdByName: string;
  createdAt: string;
  isReversed: boolean;
  reason: string | null;
  returnNo: string | null;
}

export interface PurchaseReturnSummary {
  id: string;
  returnNo: string;
  returnDate: string;
  total: number;
  originalTotal: number;
  refundedAmount: number;
}

/**
 * The subset of `ProductUnitDto` (GET products/{id}, `units`) the purchase form needs.
 * Field names are identical to the product contract so `ProductDetail` is assignable to `PurchaseProduct`.
 */
export interface PurchaseProductUnit {
  id: string;
  unitCode: string;
  displayName: string;
  decimalPlaces: number;
  conversionFactorToBase: number;
  isBaseUnit: boolean;
  isPurchasable: boolean;
  isDefaultPurchaseUnit: boolean;
  sortOrder: number;
  isActive: boolean;
}

/** The subset of `ProductDetailDto` the purchase form needs. */
export interface PurchaseProduct {
  id: string;
  baseUnitLabel: string;
  taxRatePercent: number;
  units: PurchaseProductUnit[];
}

export interface PurchaseDetail {
  id: string;
  purchaseNo: string;
  supplierId: string;
  supplierName: string;
  purchaseDate: string;
  supplierInvoiceNumber: string | null;
  notes: string | null;
  paymentMethod: PaymentMethod;
  paidAmount: number;
  dueAmount: number;
  paymentStatus: PaymentStatus;
  subtotal: number;
  taxTotal: number;
  otherChargesTotal: number;
  discountTotal: number;
  grandTotal: number;
  status: PurchaseStatus;
  createdByName: string;
  createdAt: string;
  cancelledAt: string | null;
  currencyId: string;
  currencyCode: string;
  currencySymbol: string;
  exchangeRateToBase: number;
  originalGrandTotal: number;
  originalPaidAmount: number;
  originalDueAmount: number;
  baseCurrencyCode: string;
  baseCurrencySymbol: string;
  currencyDecimalPrecision: number;
  baseCurrencyDecimalPrecision: number;
  lines: PurchaseLine[];
  payments: PurchasePayment[];
  returnedAmount: number;
  originalReturnedAmount: number;
  netTotal: number;
  originalNetTotal: number;
  originalRefundableAmount: number;
  returns: PurchaseReturnSummary[];
}

export interface CreatePurchaseLineRequest {
  productId: string;
  productUnitId: string;
  qty: number;
  rate: number;
  otherCharges: number;
  discount: number;
  supplierBatchNumber?: string | null;
}

export interface CreatePurchaseRequest {
  supplierId: string;
  purchaseDate: string;
  supplierInvoiceNumber?: string | null;
  notes?: string | null;
  paymentMethod: PaymentMethod;
  paidAmount: number;
  accountId?: string | null;
  lines: CreatePurchaseLineRequest[];
}

export interface UpdatePurchaseHeaderRequest {
  supplierInvoiceNumber?: string | null;
  notes?: string | null;
}

export interface AddPurchasePaymentRequest {
  amount: number;
  paymentMethod: PaymentMethod;
  expectedDue: number;
  accountId?: string | null;
}

export interface PurchaseRefundDetails {
  amount: number;
  paymentMethod: PaymentMethod;
  accountId?: string | null;
  reason?: string | null;
}

export interface AddPurchaseRefundRequest extends PurchaseRefundDetails {
  expectedRefundable: number;
  purchaseReturnId?: string | null;
}

export interface CreatePurchaseReturnRequest {
  purchaseOrderId: string;
  returnDate: string;
  reason?: string | null;
  lines: { purchaseOrderLineId: string; qty: number }[];
  refund?: PurchaseRefundDetails | null;
}

export interface PurchaseReturnListItem {
  id: string;
  returnNo: string;
  returnDate: string;
  purchaseOrderId: string;
  purchaseNo: string;
  supplierName: string;
  total: number;
  originalTotal: number;
  currencyCode: string;
  refundedAmount: number;
}

export interface PurchaseReturnLine {
  id: string;
  productName: string;
  unitLabel: string;
  qty: number;
  baseQty: number;
  baseUnitLabel: string;
  taxableValue: number;
  taxAmount: number;
  lineTotal: number;
}

export interface PurchaseReturnDetail {
  id: string;
  returnNo: string;
  returnDate: string;
  purchaseOrderId: string;
  purchaseNo: string;
  supplierName: string;
  reason: string | null;
  taxableTotal: number;
  taxTotal: number;
  total: number;
  originalTotal: number;
  refundedAmount: number;
  currencyCode: string;
  currencySymbol: string;
  currencyDecimalPrecision: number;
  baseCurrencyCode: string;
  baseCurrencySymbol: string;
  baseCurrencyDecimalPrecision: number;
  createdByName: string;
  createdAt: string;
  lines: PurchaseReturnLine[];
  refunds: PurchasePayment[];
}
