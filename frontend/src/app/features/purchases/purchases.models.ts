export type PurchaseStatus = 'Completed' | 'Cancelled';
export type PaymentStatus = 'Due' | 'PartiallyPaid' | 'Paid';
export type PaymentMethod = 'Cash' | 'Upi' | 'Card' | 'Credit' | 'BankTransfer';

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
  sellingModeId: string;
  sellingModeLabel: string;
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
  lines: PurchaseLine[];
}

export interface CreatePurchaseLineRequest {
  productId: string;
  sellingModeId: string;
  qty: number;
  rate: number;
  otherCharges: number;
  discount: number;
}

export interface CreatePurchaseRequest {
  supplierId: string;
  purchaseDate: string;
  supplierInvoiceNumber?: string | null;
  notes?: string | null;
  paymentMethod: PaymentMethod;
  paidAmount: number;
  lines: CreatePurchaseLineRequest[];
}

export interface UpdatePurchaseHeaderRequest {
  supplierInvoiceNumber?: string | null;
  notes?: string | null;
  paymentMethod: PaymentMethod;
  paidAmount: number;
}
