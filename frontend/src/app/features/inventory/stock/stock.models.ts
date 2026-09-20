export type StockStatus = 'Normal' | 'Low' | 'OutOfStock';
export type StockMovementType = 'Purchase' | 'Sale' | 'Adjustment';

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
}

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
