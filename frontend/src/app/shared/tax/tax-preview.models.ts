import type { TaxCalculationMethod, TaxComponentKind, TaxTreatment } from '../../features/inventory/tax-setup/tax-setup.models';

/** Mirrors backend enums (`TaxEnums.cs`), persisted as strings. */
export type TaxDirection = 'Sale' | 'Purchase';
export type PlaceOfSupply = 'IntraState' | 'InterState' | 'NotApplicable';
export type TaxRegime = 'Gst' | 'Vat';
export type TaxScope = string;

/**
 * One tax row of a document line, as shown by `LineTaxBreakdown`. Satisfied by `TaxRowDto` (preview) and by
 * `PurchaseLineTaxDto` (saved lines). For a per-base-unit row `baseAmount` is the base quantity, not money.
 */
export interface TaxRowView {
  componentId?: string | null;
  code: string;
  kind: TaxComponentKind;
  method: TaxCalculationMethod;
  ratePercent: number | null;
  amountPerBaseUnit: number | null;
  baseAmount: number;
  taxAmount: number;
  /** A row of a document saved before the calculator (no component, no treatment). */
  isLegacy?: boolean;
  treatment?: TaxTreatment | null;
}

/** Mirrors `TaxPreviewLineRequest`. */
export interface TaxPreviewLineRequest {
  productId: string;
  productUnitId: string;
  qty: number;
  unitPrice: number;
  discount: number;
  otherCharges?: number | null;
}

/** Mirrors `TaxDocumentPreviewRequest`. At most one of the bill discount percent and amount. */
export interface TaxDocumentPreviewRequest {
  direction: TaxDirection;
  partyId?: string | null;
  date: string;
  ratesIncludeTax?: boolean;
  billDiscountPercent?: number | null;
  billDiscountAmount?: number | null;
  billCharges?: number | null;
  lines: TaxPreviewLineRequest[];
}

/** Mirrors `TaxPreviewLineDto`. `taxable` includes the bill charge share and other charges. */
export interface TaxPreviewLine {
  index: number;
  productId: string;
  productName: string;
  productUnitId: string;
  unitCode: string;
  baseQty: number;
  profileId: string;
  profileCode: string;
  profileName: string;
  versionId: string;
  versionEffectiveFrom: string;
  source: TaxScope;
  sourceName: string;
  hsnCode: string | null;
  hsnSource: string;
  priceIncludesTax: boolean;
  gross: number;
  lineDiscount: number;
  billDiscountShare: number;
  billChargeShare: number;
  otherCharges: number;
  taxable: number;
  taxRatePercent: number;
  taxTotal: number;
  lineTotal: number;
  rows: TaxRowView[];
}

export interface TaxPreviewWarning {
  field: string;
  message: string;
}

/** Mirrors `TaxDocumentPreviewDto`. */
export interface TaxDocumentPreview {
  direction: TaxDirection;
  placeOfSupply: PlaceOfSupply;
  regime: TaxRegime;
  countryId: string;
  currencyCode: string;
  precision: number;
  subtotal: number;
  discountTotal: number;
  chargesTotal: number;
  taxableTotal: number;
  taxTotal: number;
  grandTotal: number;
  billDiscountAmount: number;
  effectiveBillDiscountPercent: number;
  lines: TaxPreviewLine[];
  warnings: TaxPreviewWarning[];
}
