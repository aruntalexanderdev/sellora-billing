export type StockStatus = 'Normal' | 'Low' | 'OutOfStock';
import type { StockMovementType, StockReasonCode } from '../../../shared/units/stock-movement';

export type { StockMovementType, StockReasonCode };

export interface StockOverviewItem {
  productId: string;
  sku: string;
  name: string;
  category: string | null;
  baseUnitLabel: string;
  quantityOnHand: number;
  averageCost: number;
  stockValue: number;
  status: StockStatus;
}

export interface StockMovement {
  id: string;
  occurredAt: string;
  type: StockMovementType;
  quantityBaseUnits: number;
  referenceType: string;
  referenceNo: string;
  userName: string;
  notes: string | null;
  purchaseBatchId: string | null;
  internalBatchNo: string | null;
  reasonCode: StockReasonCode | null;
  enteredQuantity: number | null;
  enteredProductUnitId: string | null;
  enteredUnitCode: string | null;
  conversionFactorSnapshot: number | null;
  enteredDecimalPlaces: number | null;
  /** Base unit decimal places; optional until the backend sends it on stock detail. */
  baseDecimalPlaces?: number;
}

export interface StockBatch {
  id: string;
  createdAt: string;
  sourceType: 'Purchase' | 'Adjustment';
  sourceRefNo: string;
  purchasedBaseQty: number;
  remainingBaseQty: number;
  unitCost: number;
  internalBatchNo: string;
  supplierBatchNumber: string | null;
  receivedDate: string;
  purchaseOrderId: string | null;
  purchaseOrderLineId: string | null;
  /** Set when the batch was created by a stock adjustment (optional field added in Phase 3). */
  stockAdjustmentId?: string | null;
}

export interface BatchConsumption {
  salesInvoiceId: string;
  invoiceNo: string;
  invoiceDate: string;
  saleStatus: 'Completed' | 'Cancelled';
  productName: string;
  qtyBaseUnits: number;
  returnedQtyBaseUnits: number;
  unitCost: number;
}

export type BatchStatus = 'Available' | 'Partly used' | 'Depleted';

export interface StockDetail {
  productId: string;
  sku: string;
  name: string;
  category: string | null;
  baseUnitLabel: string;
  taxLabel: string;
  quantityOnHand: number;
  averageCost: number;
  stockValue: number;
  reorderLevel: number;
  status: StockStatus;
  movements: StockMovement[];
  batches: StockBatch[];
}
