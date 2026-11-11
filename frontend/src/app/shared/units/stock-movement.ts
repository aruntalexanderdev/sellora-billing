import { formatQuantity } from './unit-conversion';

/** Mirrors backend `StockMovementType` (string enum). Single source for stock detail and the movement report. */
export const STOCK_MOVEMENT_TYPES = [
  'Purchase',
  'Sale',
  'Adjustment',
  'SaleReturn',
  'PurchaseReturn',
  'SaleCancellation',
  'PurchaseCancellation',
  'OpeningStock',
  'TransferOut',
  'TransferIn',
] as const;
export type StockMovementType = (typeof STOCK_MOVEMENT_TYPES)[number];

/** Mirrors backend `AdjustmentType`, carried on movements as `reasonCode`. */
export type StockReasonCode = 'Damage' | 'Loss' | 'Correction' | 'Expiry' | 'OpeningStock';

export const REASON_CODE_LABELS: Record<StockReasonCode, string> = {
  Damage: 'Damage',
  Loss: 'Loss',
  Correction: 'Correction',
  Expiry: 'Expiry',
  OpeningStock: 'Opening stock',
};

export type MovementTone = 'good' | 'info' | 'warn';

const TONES: Record<StockMovementType, MovementTone> = {
  Purchase: 'good',
  SaleReturn: 'good',
  SaleCancellation: 'good',
  OpeningStock: 'good',
  Sale: 'info',
  PurchaseReturn: 'info',
  PurchaseCancellation: 'warn',
  Adjustment: 'warn',
  TransferOut: 'info',
  TransferIn: 'good',
};

const TYPE_LABELS: Record<StockMovementType, string> = {
  Purchase: 'Purchase',
  Sale: 'Sale',
  Adjustment: 'Adjustment',
  SaleReturn: 'Sale return',
  PurchaseReturn: 'Purchase return',
  SaleCancellation: 'Sale cancellation',
  PurchaseCancellation: 'Purchase cancellation',
  OpeningStock: 'Opening stock',
  TransferOut: 'Transfer out',
  TransferIn: 'Transfer in',
};

export function movementTone(type: StockMovementType): MovementTone {
  return TONES[type] ?? 'warn';
}

/** "Adjustment - Damage" for adjustments with a reason; plain label otherwise. */
export function movementTypeLabel(type: StockMovementType, reasonCode?: StockReasonCode | null): string {
  const label = TYPE_LABELS[type] ?? type;
  if (reasonCode && type === 'Adjustment') return `${label} - ${REASON_CODE_LABELS[reasonCode] ?? reasonCode}`;
  return label;
}

/** Entered-unit snapshot fields present on movement DTOs (all null for legacy rows). */
export interface MovementEnteredFields {
  enteredQuantity?: number | null;
  enteredProductUnitId?: string | null;
  enteredUnitCode?: string | null;
  conversionFactorSnapshot?: number | null;
  enteredDecimalPlaces?: number | null;
}

/**
 * "2 BOX (x10)" when the entered quantity is known, "in BOX" when only the unit is known
 * (movement covers part of a line), null for legacy rows (show base quantity only).
 */
export function enteredLabel(m: MovementEnteredFields): string | null {
  if (!m.enteredUnitCode) return null;
  const factor = m.conversionFactorSnapshot;
  const factorText = factor != null && factor !== 1 ? ` (x${+factor.toFixed(6)})` : '';
  if (m.enteredQuantity == null) return `in ${m.enteredUnitCode}`;
  return `${formatQuantity(Math.abs(m.enteredQuantity), m.enteredDecimalPlaces)} ${m.enteredUnitCode}${factorText}`;
}
