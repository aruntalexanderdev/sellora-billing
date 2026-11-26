import type { BranchFields, LocationFields } from '../../shared/scope/scope.models';
import type { TaxComponentKind } from '../inventory/tax-setup/tax-setup.models';
export type StockStatus = 'Normal' | 'Low' | 'OutOfStock';
import type { PlaceOfSupply } from '../../shared/tax/tax-preview.models';
import type { TaxTreatment } from '../inventory/tax-setup/tax-setup.models';
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
  /** TP-3: always present on current servers (history falls back to subtotal + other - discount). */
  taxableTotal?: number;
  /** Tax per component kind; rows saved before the calculator are under `Other`. */
  taxByKind?: PurchaseReportTax[] | null;
}

/** Mirrors `PurchaseReportTaxDto`. */
export interface PurchaseReportTax {
  kind: TaxComponentKind;
  taxAmount: number;
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
  /** TP-4: always present on current servers (history falls back to subtotal - discount). */
  taxableTotal?: number;
  /** Tax per component kind; rows saved before the calculator are under `Other`. */
  taxByKind?: SalesReportTax[] | null;
}

/** Mirrors `SalesReportTaxDto`. */
export interface SalesReportTax {
  kind: TaxComponentKind;
  taxAmount: number;
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

// ---- TP-5a tax reports (Api/Contracts/TaxReportContracts.cs)
export type GstReportSection = 'Outward' | 'OutwardReturn' | 'Inward' | 'InwardReturn';
export type VatReportSection = 'Output' | 'OutputReturn' | 'Input' | 'InputReturn';
export type VatCategory = 'StandardRated' | 'ZeroRated' | 'Exempt' | 'OutOfScope' | 'Legacy';
export type HsnDirection = 'Sales' | 'Purchases';
export type TaxLimitationCode = 'NoPartyGstin' | 'InputTaxAsInvoiced' | 'NoPartyTaxRegistration' | 'HsnNotRecorded';

export interface TaxReportLimitation {
  code: TaxLimitationCode | string;
  message: string;
}

export interface TaxReportAmounts {
  taxableValue: number;
  cgst: number;
  sgstUtgst: number;
  igst: number;
  cess: number;
  legacyTax: number;
  totalTax: number;
}

export interface GstSummaryRow {
  section: GstReportSection;
  gstin: string | null;
  placeOfSupplyStateId: string | null;
  placeOfSupplyStateName: string | null;
  placeOfSupplyStateCode: string | null;
  supplyType: PlaceOfSupply | null;
  treatment: TaxTreatment | null;
  ratePercent: number;
  isLegacy: boolean;
  taxableValue: number;
  cgst: number;
  sgstUtgst: number;
  igst: number;
  cess: number;
  legacyTax: number;
  documentCount: number;
}

export interface GstSectionTotal {
  section: GstReportSection;
  amounts: TaxReportAmounts;
  documentCount: number;
}

export interface GstGstinBlock {
  gstin: string | null;
  sections: GstSectionTotal[];
  outwardNet: TaxReportAmounts;
  inwardNet: TaxReportAmounts;
}

export interface GstSummary {
  dateFrom: string;
  dateTo: string;
  branchId: string | null;
  currencyCode: string;
  rows: GstSummaryRow[];
  blocks: GstGstinBlock[];
  limitations: TaxReportLimitation[];
}

export interface HsnSummaryRow {
  /** Null = HSN not recorded (lines predating HSN snapshots). */
  hsnCode: string | null;
  unitId: string;
  unitCode: string;
  uqc: string;
  decimalPlaces: number;
  ratePercent: number;
  baseQty: number;
  taxableValue: number;
  cgst: number;
  sgstUtgst: number;
  igst: number;
  cess: number;
  legacyTax: number;
  totalValue: number;
}

export interface HsnSummary {
  dateFrom: string;
  dateTo: string;
  branchId: string | null;
  direction: HsnDirection;
  currencyCode: string;
  rows: HsnSummaryRow[];
  totals: { taxableValue: number; totalTax: number; totalValue: number };
  limitations: TaxReportLimitation[];
}

export interface VatSummaryRow {
  section: VatReportSection;
  category: VatCategory;
  regionStateId: string | null;
  regionName: string | null;
  ratePercent: number;
  taxableValue: number;
  vatAmount: number;
  documentCount: number;
}

export interface VatSectionTotal {
  section: VatReportSection;
  taxableValue: number;
  vatAmount: number;
  documentCount: number;
}

export interface VatNet {
  taxableValue: number;
  vatAmount: number;
}

export interface VatSummary {
  dateFrom: string;
  dateTo: string;
  branchId: string | null;
  currencyCode: string;
  rows: VatSummaryRow[];
  sections: VatSectionTotal[];
  outputVat: VatNet;
  inputVat: VatNet;
  limitations: TaxReportLimitation[];
}
