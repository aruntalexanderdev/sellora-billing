export type StockStatus = 'Normal' | 'Low' | 'OutOfStock';
export type StockMovementType = 'Purchase' | 'Sale' | 'Adjustment' | 'SaleReturn' | 'PurchaseReturn';
export type PurchaseStatus = 'Completed' | 'Cancelled';
export type SalesStatus = 'Completed' | 'Cancelled';
export type PaymentMethod = 'Cash' | 'Upi' | 'Card' | 'Credit' | 'BankTransfer';

export interface CurrentStockReportRow {
  productId: string;
  sku: string;
  name: string;
  category: string | null;
  baseUnitLabel: string;
  quantityOnHand: number;
  averageCost: number;
  stockValue: number;
  sellingPricePerBaseUnit: number | null;
  sellingValue: number | null;
  status: StockStatus;
}

export interface StockMovementReportRow {
  id: string;
  occurredAt: string;
  productName: string;
  sku: string;
  baseUnitLabel: string;
  type: StockMovementType;
  quantityBaseUnits: number;
  referenceType: string;
  referenceNo: string;
  userName: string;
}

export interface PurchaseReportRow {
  id: string;
  purchaseNo: string;
  purchaseDate: string;
  supplierName: string;
  subtotal: number;
  taxTotal: number;
  otherChargesTotal: number;
  grandTotal: number;
  paidAmount: number;
  dueAmount: number;
  status: PurchaseStatus;
  currencyCode: string;
  originalGrandTotal: number;
}

export interface SalesReportRow {
  id: string;
  invoiceNo: string;
  invoiceDate: string;
  customerName: string;
  subtotal: number;
  taxTotal: number;
  discountTotal: number;
  grandTotal: number;
  paidAmount: number;
  dueAmount: number;
  status: SalesStatus;
}

export interface ProfitSummary {
  dateFrom: string | null;
  dateTo: string | null;
  invoiceCount: number;
  salesRevenue: number;
  costOfGoodsSold: number;
  grossProfit: number;
  grossMarginPercent: number | null;
}
