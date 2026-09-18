export type AdjustmentType = 'Damage' | 'Loss' | 'Correction' | 'Expiry' | 'OpeningStock';
export type AdjustmentDirection = 'Increase' | 'Decrease';

export interface CreateStockAdjustmentRequest {
  productId: string;
  type: AdjustmentType;
  direction: AdjustmentDirection;
  quantityBaseUnits: number;
  unitCost?: number | null;
  reason?: string | null;
}

export interface StockAdjustmentResult {
  id: string;
  adjustmentNo: string;
  productId: string;
  productName: string;
  type: AdjustmentType;
  direction: AdjustmentDirection;
  quantityBaseUnits: number;
  unitCost: number | null;
  reason: string | null;
  beforeQty: number;
  afterQty: number;
  userName: string;
  createdAt: string;
}
