import type { BranchFields, LocationFields } from '../../shared/scope/scope.models';
export type StockStatus = 'Normal' | 'Low' | 'OutOfStock';
import type { BreakdownUnit } from '../../shared/units/unit.models';
import type { StockMovementType, StockReasonCode } from '../../shared/units/stock-movement';

export type { StockMovementType, StockReasonCode };
export type PurchaseStatus = 'Completed' | 'Cancelled';
export type SalesStatus = 'Completed' | 'Cancelled';
export type PaymentMethod = 'Cash' | 'Upi' | 'Card' | 'Credit' | 'BankTransfer';

export interface CurrentStockReportRow {
  productId: string;
  sku: string;
  name: string;
  category: string | null;
  categoryId?: string | null;
  subcategoryName?: string | null;
  baseUnitLabel: string;
  baseDecimalPlaces: number;
  breakdownUnits?: BreakdownUnit[];
  quantityOnHand: number;
  averageCost: number;
  stockValue: number;
  sellingPricePerBaseUnit: number | null;
  sellingValue: number | null;
  status: StockStatus;
}

export interface StockMovementReportRow extends LocationFields {
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
  reasonCode: StockReasonCode | null;
  enteredQuantity: number | null;
  enteredProductUnitId: string | null;
  enteredUnitCode: string | null;
  conversionFactorSnapshot: number | null;
  enteredDecimalPlaces: number | null;
  baseDecimalPlaces: number;
  /** Source document id (additive G2); set for StockTransfer movements. */
  referenceId?: string | null;
}

export interface PurchaseReportRow extends BranchFields {
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

export interface SalesReportRow extends BranchFields {
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
  branchId?: string | null;
}
