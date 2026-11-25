import type { BranchFields, LocationFields } from '../../shared/scope/scope.models';
import type { PlaceOfSupply, TaxRowView } from '../../shared/tax/tax-preview.models';

/** Mirrors the backend `PartyTaxStatus` (only `Unspecified` exists until the party-status phase). */
export type PartyTaxStatus = 'Unspecified';

/** Mirrors `PurchaseLineTaxDto` (a purchase or purchase-return line row). `isLegacy` rows predate the calculator. */
export interface PurchaseLineTax extends TaxRowView {
  originalBaseAmount: number | null;
  originalTaxAmount: number | null;
}

/** Mirrors `PurchaseWarningDto`: field `supplierId` or `lines[i].hsnCode`. Only the create response carries them. */
export interface PurchaseWarning {
  field: string;
  message: string;
}

export type PurchaseStatus = 'Completed' | 'Cancelled';
export type PaymentStatus = 'Due' | 'PartiallyPaid' | 'Paid';
export type PaymentMethod = 'Cash' | 'Upi' | 'Card' | 'Credit' | 'BankTransfer';
export type PaymentEntryKind = 'Payment' | 'Refund' | 'Reversal';

export interface PurchaseListItem extends BranchFields, LocationFields {
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
  unitCode: string;
  conversionFactorToBase: number;
  unitDecimalPlaces: number;
  qty: number;
  baseQty: number;
  baseUnitLabel: string;
  baseUnitDecimalPlaces: number;
  rate: number;
  taxRatePercent: number;
  otherCharges: number;
  discount: number;
  taxableValue: number;
  taxAmount: number;
  lineTotal: number;
  unitCost: number;
  returnedBaseQty: number;
  returnableBaseQty: number;
  /** Units a return may be entered in (empty when cancelled or nothing is returnable). */
  returnUnits: PurchaseReturnUnit[];
  internalBatchNo: string | null;
  supplierBatchNumber: string | null;
  receivedDate: string | null;
  /** Phase 10: per visible location, the base qty of this line's batch still held there (> 0 only). Present with a location in scope. */
  locationBalances?: PurchaseLineLocationBalance[] | null;
  // ---- TP-3 tax snapshot (absent / empty on history)
  taxes?: PurchaseLineTax[] | null;
  taxProfileId?: string | null;
  taxProfileName?: string | null;
  taxProfileVersionId?: string | null;
  placeOfSupply?: PlaceOfSupply | null;
  priceIncludesTax?: boolean;
  hsnCode?: string | null;
  billDiscountShare?: number;
  billChargeShare?: number;
  originalRate?: number | null;
  originalDiscount?: number | null;
  originalOtherCharges?: number | null;
  originalBillDiscountShare?: number | null;
  originalBillChargeShare?: number | null;
  originalTaxableValue?: number | null;
  originalTaxAmount?: number | null;
  originalLineTotal?: number | null;
}

/** Mirrors `PurchaseLineLocationBalanceDto`. */
export interface PurchaseLineLocationBalance {
  stockLocationId: string;
  code: string;
  name: string;
  baseQty: number;
}

export interface PurchaseReturnUnit {
  productUnitId: string;
  unitCode: string;
  displayName: string;
  decimalPlaces: number;
  conversionFactorToBase: number;
  isLineUnit: boolean;
  /** Returnable base quantity / factor, truncated to the unit's decimals. Guidance only. */
  maxQty: number;
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

export interface PurchaseDetail extends BranchFields, LocationFields {
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
  // ---- TP-3 tax snapshot. `taxableTotal` is always present (the server falls back for history); bill amounts are in the base currency.
  taxableTotal?: number;
  ratesIncludeTax?: boolean;
  billDiscountPercent?: number | null;
  billDiscountAmount?: number;
  billCharges?: number;
  originalBillDiscountAmount?: number;
  originalBillCharges?: number;
  placeOfSupply?: PlaceOfSupply | null;
  placeOfSupplyStateId?: string | null;
  placeOfSupplyStateName?: string | null;
  supplierStateId?: string | null;
  supplierStateName?: string | null;
  partyTaxStatus?: PartyTaxStatus;
  /** Create response only; GET returns none. */
  warnings?: PurchaseWarning[] | null;
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
  /** Receiving location; omitted/null = the working branch's default store. */
  stockLocationId?: string | null;
  lines: CreatePurchaseLineRequest[];
  // ---- TP-3 (all optional). At most one of the bill discount percent and amount.
  ratesIncludeTax?: boolean;
  billDiscountPercent?: number | null;
  billDiscountAmount?: number | null;
  billCharges?: number;
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
  /** Location the goods leave from; omitted/null = server default. */
  stockLocationId?: string | null;
  lines: { purchaseOrderLineId: string; productUnitId?: string | null; qty: number }[];
  refund?: PurchaseRefundDetails | null;
}

export interface PurchaseReturnListItem extends BranchFields, LocationFields {
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
  productUnitId: string;
  unitLabel: string;
  unitCode: string;
  conversionFactorToBase: number;
  qty: number;
  baseQty: number;
  baseUnitLabel: string;
  baseUnitDecimalPlaces: number;
  taxableValue: number;
  taxAmount: number;
  lineTotal: number;
  // ---- TP-3: the reversed rows of the original line (absent on history)
  taxes?: PurchaseLineTax[] | null;
  taxProfileId?: string | null;
  taxProfileVersionId?: string | null;
  placeOfSupply?: PlaceOfSupply | null;
  originalTaxableValue?: number | null;
  originalTaxAmount?: number | null;
}

export interface PurchaseReturnDetail extends BranchFields, LocationFields {
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
