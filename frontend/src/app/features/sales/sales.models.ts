export type SalesStatus = 'Completed' | 'Cancelled';
export type PaymentStatus = 'Due' | 'PartiallyPaid' | 'Paid';
export type PaymentMethod = 'Cash' | 'Upi' | 'Card' | 'Credit' | 'BankTransfer';
export type PaymentEntryKind = 'Payment' | 'Refund' | 'Reversal';

export interface SaleCatalogMode {
  id: string;
  label: string;
  conversionFactorToBase: number;
  currentPrice: number | null;
}

export interface SaleCatalogProduct {
  productId: string;
  sku: string;
  name: string;
  category: string | null;
  baseUnitLabel: string;
  quantityOnHand: number;
  taxRatePercent: number;
  sellingModes: SaleCatalogMode[];
}

export interface SalesInvoiceListItem {
  id: string;
  invoiceNo: string;
  customerName: string;
  invoiceDate: string;
  grandTotal: number;
  paidAmount: number;
  dueAmount: number;
  paymentStatus: PaymentStatus;
  status: SalesStatus;
}

export interface SalesInvoiceLine {
  id: string;
  productId: string;
  productName: string;
  sellingModeId: string;
  sellingModeLabel: string;
  qty: number;
  baseQty: number;
  baseUnitLabel: string;
  unitPrice: number;
  taxRatePercent: number;
  discount: number;
  taxableValue: number;
  taxAmount: number;
  lineTotal: number;
  costOfGoodsSold: number;
  returnedQty: number;
  returnableQty: number;
}

export interface SalesPayment {
  id: string;
  kind: PaymentEntryKind;
  paymentDate: string;
  amount: number;
  paymentMethod: PaymentMethod;
  createdByName: string;
  createdAt: string;
  isReversed: boolean;
  reason: string | null;
  returnNo: string | null;
}

export interface SalesReturnSummary {
  id: string;
  returnNo: string;
  returnDate: string;
  total: number;
  refundedAmount: number;
}

export interface SalesInvoiceDetail {
  id: string;
  invoiceNo: string;
  customerId: string;
  customerName: string;
  invoiceDate: string;
  paymentMethod: PaymentMethod;
  paidAmount: number;
  dueAmount: number;
  paymentStatus: PaymentStatus;
  subtotal: number;
  taxTotal: number;
  discountTotal: number;
  grandTotal: number;
  status: SalesStatus;
  createdByName: string;
  createdAt: string;
  cancelledAt: string | null;
  lines: SalesInvoiceLine[];
  payments: SalesPayment[];
  returnedAmount: number;
  netTotal: number;
  refundableAmount: number;
  returns: SalesReturnSummary[];
}

export interface CreateSaleLineRequest {
  productId: string;
  sellingModeId: string;
  qty: number;
  unitPrice: number;
  discount: number;
}

export interface CreateSaleRequest {
  customerId: string;
  invoiceDate: string;
  paymentMethod: PaymentMethod;
  paidAmount: number;
  accountId?: string | null;
  lines: CreateSaleLineRequest[];
}

export interface AddSalePaymentRequest {
  amount: number;
  paymentMethod: PaymentMethod;
  expectedDue: number;
  accountId?: string | null;
}

export interface RefundDetails {
  amount: number;
  paymentMethod: PaymentMethod;
  accountId?: string | null;
  reason?: string | null;
}

export interface AddSaleRefundRequest extends RefundDetails {
  expectedRefundable: number;
  salesReturnId?: string | null;
}

export interface CreateSaleReturnRequest {
  salesInvoiceId: string;
  returnDate: string;
  reason?: string | null;
  lines: { salesInvoiceLineId: string; qty: number }[];
  refund?: RefundDetails | null;
}

export interface SalesReturnListItem {
  id: string;
  returnNo: string;
  returnDate: string;
  salesInvoiceId: string;
  invoiceNo: string;
  customerName: string;
  total: number;
  refundedAmount: number;
}

export interface SalesReturnLine {
  id: string;
  productName: string;
  sellingModeLabel: string;
  qty: number;
  baseQty: number;
  baseUnitLabel: string;
  taxableValue: number;
  taxAmount: number;
  lineTotal: number;
}

export interface SalesReturnDetail {
  id: string;
  returnNo: string;
  returnDate: string;
  salesInvoiceId: string;
  invoiceNo: string;
  customerName: string;
  reason: string | null;
  taxableTotal: number;
  taxTotal: number;
  total: number;
  refundedAmount: number;
  createdByName: string;
  createdAt: string;
  lines: SalesReturnLine[];
  refunds: SalesPayment[];
}
