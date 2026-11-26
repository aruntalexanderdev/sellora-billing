import type { MeasurementCategory } from '../../masters/units/units.models';
import type { FieldWarning, TaxProfileRef, TaxScope } from '../tax-setup/tax-setup.models';

/** Mirrors backend `ProductUnitDto.LockReason` (string). */
export type ProductUnitLockReason = 'Transactions' | 'Prices' | 'ContainedInLockedUnit';

/** Mirrors backend `ProductUnitDto`. */
export interface ProductUnit {
  id: string;
  unitId: string;
  unitCode: string;
  unitName: string;
  unitCategory: MeasurementCategory;
  decimalPlaces: number;
  /** Stored inner row id; null only for the base row. */
  innerProductUnitId: string | null;
  /** UnitId of the inner row; null only for the base row. */
  innerUnitId: string | null;
  innerQuantity: number;
  conversionFactorToBase: number;
  isBaseUnit: boolean;
  label: string | null;
  /** `label ?? unitName`. */
  displayName: string;
  barcode: string | null;
  isPurchasable: boolean;
  isSellable: boolean;
  isDefaultPurchaseUnit: boolean;
  isDefaultSalesUnit: boolean;
  sortOrder: number;
  depth: number;
  isActive: boolean;
  /** Referenced by transactions/prices (or contained in such a unit): structure is immutable. */
  isLocked: boolean;
  lockReason: ProductUnitLockReason | null;
}

/**
 * Mirrors backend `UpsertProductUnitRequest`.
 * The base row is the one with `innerUnitId === null` and `unitId === baseUnitId`.
 * Inner rows are referenced by `innerUnitId`, resolved among the submitted active rows.
 */
export interface ProductUnitInput {
  id: string | null;
  unitId: string;
  innerUnitId: string | null;
  innerQuantity: number | null;
  label: string | null;
  barcode: string | null;
  isPurchasable: boolean;
  isSellable: boolean;
  isDefaultPurchaseUnit: boolean;
  isDefaultSalesUnit: boolean;
  isActive: boolean;
}

export interface ProductListItem {
  id: string;
  sku: string;
  name: string;
  /** Category name (legacy field, mirrors `categoryName`). */
  category: string | null;
  categoryId: string | null;
  categoryName: string | null;
  subcategoryId: string | null;
  subcategoryName: string | null;
  baseUnitLabel: string;
  taxLabel: string;
  reorderLevel: number;
  isActive: boolean;
  /** No history (never purchased, sold, adjusted, transferred, no stock on hand): DELETE would succeed. */
  canDelete: boolean;
}

/** Mirrors backend `ProductDetailDto`. */
export interface ProductDetail {
  id: string;
  sku: string;
  name: string;
  category: string | null;
  categoryId: string | null;
  categoryName: string | null;
  subcategoryId: string | null;
  subcategoryName: string | null;
  baseUnitId: string;
  baseUnitLabel: string;
  taxId: string;
  taxLabel: string;
  taxRatePercent: number;
  reorderLevel: number;
  isActive: boolean;
  /** True when stock history exists or any unit row is locked: the base unit cannot change. */
  isBaseUnitLocked: boolean;
  /** Ordered by `sortOrder` (base first). */
  units: ProductUnit[];
  // Tax profiles (TP-1): what the product resolves to for the company country. All null when none applies.
  /** Own HSN/SAC code. */
  hsnCode: string | null;
  /** Own code or the subcategory's. */
  effectiveHsnCode: string | null;
  /** `Product` | `Subcategory` | `None`. */
  hsnSource: string | null;
  /** Null = follows the company setting. */
  priceIncludesTax: boolean | null;
  salesTaxProfile: TaxProfileRef | null;
  purchaseTaxProfile: TaxProfileRef | null;
  taxSource: TaxScope | null;
  taxSourceName: string | null;
  /** Own overrides (null = inherited). */
  salesOverrideProfileId: string | null;
  purchaseOverrideProfileId: string | null;
  /** Field `hsnCode` (T11). */
  warnings: FieldWarning[] | null;
}

/** Mirrors backend `UpsertProductRequest`. */
export interface UpsertProductRequest {
  sku: string;
  name: string;
  categoryId: string | null;
  subcategoryId: string | null;
  baseUnitId: string;
  reorderLevel: number;
  units: ProductUnitInput[];
  /** 4/6/8 digits; null = inherit from the subcategory. */
  hsnCode: string | null;
  /** Null = follow the company setting. */
  priceIncludesTax: boolean | null;
  /** Always sent: omitting (null) removes a stored override. */
  salesTaxProfileId: string | null;
  /** Needs a sales override; null = same as sales. */
  purchaseTaxProfileId: string | null;
}

/** Mirrors backend `UnitPreviewRequest` (`POST products/unit-preview`). */
export interface UnitPreviewRequest {
  productId: string | null;
  baseUnitId: string;
  units: ProductUnitInput[];
}

/** Mirrors backend `FieldErrorDto`. `field` looks like `Units[2].InnerQuantity`, `Units` or `BaseUnitId`. */
export interface FieldError {
  field: string;
  message: string;
}

/** Mirrors backend `UnitPreviewRowDto`. `index` is the position in the submitted `units` array. */
export interface UnitPreviewRow {
  index: number;
  unitId: string;
  unitCode: string;
  displayName: string;
  conversionFactorToBase: number;
  depth: number;
  sortOrder: number;
  /** e.g. "1 Full Carton = 2 Half Carton = 10 Box = 100 Piece". */
  chain: string;
  isLocked: boolean;
}

/** Mirrors backend `UnitPreviewResponse`. Returned with 200 even when `isValid` is false. */
export interface UnitPreviewResponse {
  isValid: boolean;
  rows: UnitPreviewRow[];
  errors: FieldError[];
  warnings: FieldError[];
}
