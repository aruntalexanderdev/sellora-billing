import { convert, formatQuantity, hasAtMostDecimals, round10 } from './unit-conversion';

/** A unit a return line may be entered in (mirrors `PurchaseReturnUnitDto` / `SaleReturnUnitDto`). */
export interface ReturnUnit {
  productUnitId: string;
  unitCode: string;
  displayName: string;
  decimalPlaces: number;
  conversionFactorToBase: number;
  isLineUnit: boolean;
  /** Returnable base quantity / factor, truncated to the unit's decimals. Guidance only. */
  maxQty: number;
}

// ---------------------------------------------------------------------------
// Returns (purchase Phase 4, sales Phase 5): entered in any allowed unit, validated and prorated in base quantity.
// ---------------------------------------------------------------------------

/** The unit a return line starts in: the document line's own unit, else the first allowed unit. */
export function defaultReturnUnit<T extends ReturnUnit>(units: readonly T[]): T | undefined {
  return units.find((u) => u.isLineUnit) ?? units[0];
}

/** Base quantity of an entered return quantity (preview only; the server is authoritative). */
export function returnBaseQty(qty: number, unit: Pick<ReturnUnit, 'conversionFactorToBase'>): number {
  return convert(qty, unit.conversionFactorToBase, 1);
}

/** Credit for a partial return, prorated by base quantity (the server gives the last return the exact remainder). */
export function proratedCredit(lineTotal: number, returnBase: number, lineBaseQty: number): number {
  return lineBaseQty > 0 ? (lineTotal * returnBase) / lineBaseQty : 0;
}

/**
 * Unit and quantity for "return everything": the line's own unit when the remaining base fits its
 * decimals, otherwise the base unit (factor 1) with the remaining base quantity.
 */
export function returnAllChoice(
  line: { returnableBaseQty: number; returnUnits: readonly ReturnUnit[] },
): { productUnitId: string; qty: number } | null {
  const lineUnit = defaultReturnUnit(line.returnUnits);
  if (!lineUnit || line.returnableBaseQty <= 0) return null;
  const inLineUnit = convert(line.returnableBaseQty, 1, lineUnit.conversionFactorToBase);
  if (hasAtMostDecimals(inLineUnit, lineUnit.decimalPlaces) && inLineUnit <= lineUnit.maxQty + 1e-9) {
    return { productUnitId: lineUnit.productUnitId, qty: round10(inLineUnit) };
  }
  const base = line.returnUnits.find((u) => u.conversionFactorToBase === 1);
  return base ? { productUnitId: base.productUnitId, qty: line.returnableBaseQty } : null;
}

/** Client mirror of the server's checks for a return quantity; returns an error message or null. */
export function returnQtyError(
  qty: number | null | undefined,
  unit: ReturnUnit | undefined,
  baseDecimalPlaces: number,
  baseLabel: string,
): string | null {
  if (qty == null || !unit) return null;
  if (qty < 0) return 'Enter a positive quantity.';
  if (!hasAtMostDecimals(qty, unit.decimalPlaces)) {
    return unit.decimalPlaces === 0
      ? `${unit.displayName} is counted in whole numbers.`
      : `${unit.displayName} allows at most ${unit.decimalPlaces} decimal place(s).`;
  }
  const base = returnBaseQty(qty, unit);
  if (!hasAtMostDecimals(base, baseDecimalPlaces)) {
    return `${formatQuantity(qty, unit.decimalPlaces)} ${unit.unitCode} = ${formatQuantity(base, 3)} ${baseLabel}; ${baseLabel} is tracked to ${baseDecimalPlaces} decimal place(s).`;
  }
  if (qty > unit.maxQty) return `At most ${formatQuantity(unit.maxQty, unit.decimalPlaces)} ${unit.unitCode}.`;
  return null;
}

