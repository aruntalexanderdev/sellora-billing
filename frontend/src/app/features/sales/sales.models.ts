export type SalesStatus = 'Completed' | 'Cancelled';
export type PaymentStatus = 'Due' | 'PartiallyPaid' | 'Paid';
export type PaymentMethod = 'Cash' | 'Upi' | 'Card' | 'Credit' | 'BankTransfer';

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
}

export interface SalesPayment {
  id: string;
  paymentDate: string;
  amount: number;
  paymentMethod: PaymentMethod;
  createdByName: string;
  createdAt: string;
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
