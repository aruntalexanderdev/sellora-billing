export type SalesStatus = 'Completed' | 'Cancelled';
export type PaymentStatus = 'Due' | 'PartiallyPaid' | 'Paid';
export type PaymentMethod = 'Cash' | 'Upi' | 'Card' | 'Credit' | 'BankTransfer';
export type PaymentEntryKind = 'Payment' | 'Refund' | 'Reversal';

/** Mirrors `SaleCatalogUnitDto`: an active, sellable product unit with its current list price. */
export interface SaleCatalogUnit {
  productUnitId: string;
  unitId: string;
  unitCode: string;
  displayName: string;
  conversionFactorToBase: number;
  decimalPlaces: number;
  isBaseUnit: boolean;
  /** The product's default sales unit. */
  isDefault: boolean;
  currentPrice: number | null;
  barcode: string | null;
}

/** Mirrors `SaleBarcodeMatchDto`: the product (full catalog payload) and the exact unit the barcode identifies. */
export interface SaleBarcodeMatch {
  product: SaleCatalogProduct;
  productUnitId: string;
}

/** Mirrors `SaleReturnUnitDto`: a unit a return line may be entered in. */
export interface SaleReturnUnit {
  productUnitId: string;
  unitCode: string;
  displayName: string;
  decimalPlaces: number;
  conversionFactorToBase: number;
  isLineUnit: boolean;
  /** Returnable base quantity / factor, truncated to the unit's decimals. Guidance only. */
  maxQty: number;
}

export interface SaleCatalogProduct {
  productId: string;
  sku: string;
  name: string;
  category: string | null;
  baseUnitLabel: string;
  quantityOnHand: number;
  taxRatePercent: number;
  units: SaleCatalogUnit[];
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

/** A purchase batch a sale line drew stock from, at the batch's cost. */
export interface SaleBatchConsumption {
  purchaseBatchId: string;
  internalBatchNo: string;
  supplierBatchNumber: string | null;
  sourceType: 'Purchase' | 'Adjustment';
  sourceRefNo: string;
  purchaseOrderId: string | null;
  qtyBaseUnits: number;
  returnedQtyBaseUnits: number;
  unitCost: number;
}

export interface SalesInvoiceLine {
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
  unitPrice: number;
  /** Open list price at sale time; null on legacy lines ("not recorded"). */
  listUnitPrice: number | null;
  priceOverridden: boolean;
  taxRatePercent: number;
  discount: number;
  taxableValue: number;
  taxAmount: number;
  lineTotal: number;
  costOfGoodsSold: number;
  returnedBaseQty: number;
  returnableBaseQty: number;
  /** Units a return may be entered in (empty when cancelled or nothing is returnable). */
  returnUnits: SaleReturnUnit[];
  batchConsumptions: SaleBatchConsumption[];
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
  productUnitId: string;
  qty: number;
  unitPrice: number;
  discount: number;
  /** True when the cashier deliberately changed the price (or discounted); needs Sales.PriceOverride. */
  overridePrice: boolean;
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
  lines: { salesInvoiceLineId: string; productUnitId?: string | null; qty: number }[];
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
  unitLabel: string;
  unitCode: string;
  qty: number;
  baseQty: number;
  baseUnitLabel: string;
  baseUnitDecimalPlaces: number;
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
