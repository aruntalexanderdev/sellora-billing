import type { MeasurementCategory } from '../../masters/units/units.models';

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
  category: string | null;
  baseUnitLabel: string;
  taxLabel: string;
  reorderLevel: number;
  isActive: boolean;
}

/** Mirrors backend `ProductDetailDto`. */
export interface ProductDetail {
  id: string;
  sku: string;
  name: string;
  category: string | null;
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
}

/** Mirrors backend `UpsertProductRequest`. */
export interface UpsertProductRequest {
  sku: string;
  name: string;
  category?: string | null;
  baseUnitId: string;
  taxId: string;
  reorderLevel: number;
  units: ProductUnitInput[];
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
