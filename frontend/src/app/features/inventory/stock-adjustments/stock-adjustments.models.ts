/** Mirrors backend `AdjustmentType` (string enum). */
export type AdjustmentType = 'Damage' | 'Loss' | 'Correction' | 'Expiry' | 'OpeningStock';
/** Mirrors backend `AdjustmentDirection` (string enum). */
export type AdjustmentDirection = 'Increase' | 'Decrease';

/**
 * Mirrors backend `CreateStockAdjustmentRequest`.
 * `quantity` is entered in `productUnitId`; `unitCost` is per entered unit (the server derives the base cost).
 */
export interface CreateStockAdjustmentRequest {
  productId: string;
  productUnitId: string;
  type: AdjustmentType;
  direction: AdjustmentDirection;
  quantity: number;
  unitCost?: number | null;
  reason?: string | null;
}

/** Mirrors backend `StockAdjustmentListItemDto`. */
export interface StockAdjustmentListItem {
  id: string;
  adjustmentNo: string;
  createdAt: string;
  productId: string;
  productName: string;
  sku: string;
  type: AdjustmentType;
  direction: AdjustmentDirection;
  enteredQuantity: number;
  unitCode: string;
  enteredDecimalPlaces: number;
  quantityBaseUnits: number;
  baseUnitCode: string;
  baseDecimalPlaces: number;
  /** Cost per entered unit. */
  enteredUnitCost: number | null;
  /** Cost per base unit. */
  unitCost: number | null;
  beforeQty: number;
  afterQty: number;
  userName: string;
}

/** Mirrors backend `StockAdjustmentBatchDto`. `quantityBaseUnits` is signed (+ created, − consumed). */
export interface StockAdjustmentBatch {
  purchaseBatchId: string;
  internalBatchNo: string;
  quantityBaseUnits: number;
  unitCost: number;
}

/** Mirrors backend `StockAdjustmentDto` (GET {id} and the POST response). */
export interface StockAdjustmentDetail {
  id: string;
  adjustmentNo: string;
  createdAt: string;
  productId: string;
  productName: string;
  sku: string;
  type: AdjustmentType;
  direction: AdjustmentDirection;
  enteredQuantity: number;
  productUnitId: string;
  unitCode: string;
  unitDisplayName: string;
  conversionFactorToBase: number;
  enteredDecimalPlaces: number;
  quantityBaseUnits: number;
  baseUnitCode: string;
  baseDecimalPlaces: number;
  enteredUnitCost: number | null;
  unitCost: number | null;
  reason: string | null;
  beforeQty: number;
  afterQty: number;
  userName: string;
  batches: StockAdjustmentBatch[];
}

/** Query for `GET stock-adjustments`. Dates are `yyyy-MM-dd` (UTC day bounds server-side). */
export interface StockAdjustmentListQuery {
  search?: string;
  productId?: string;
  type?: AdjustmentType;
  direction?: AdjustmentDirection;
  dateFrom?: string;
  dateTo?: string;
  page?: number;
  pageSize?: number;
}
