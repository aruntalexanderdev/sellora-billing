import type { PurchaseProductUnit } from '../purchases.models';
import { convert, formatQuantity, hasAtMostDecimals } from '../../../shared/units/unit-conversion';

/** Units a purchase line may be entered in: active and purchasable, in hierarchy (`sortOrder`) order. */
export function purchaseUnitOptions<T extends PurchaseProductUnit>(units: readonly T[] | null | undefined): T[] {
  return (units ?? []).filter((u) => u.isActive && u.isPurchasable).sort((a, b) => a.sortOrder - b.sortOrder);
}

/** Preselected purchase unit: the default purchase unit, otherwise the base unit, otherwise the first option. */
export function defaultPurchaseUnit<T extends PurchaseProductUnit>(units: readonly T[] | null | undefined): T | undefined {
  const options = purchaseUnitOptions(units);
  return options.find((u) => u.isDefaultPurchaseUnit) ?? options.find((u) => u.isBaseUnit) ?? options[0];
}

// Return helpers are shared with sales returns (Phase 5) and live in shared/units/return-units.ts.
export { defaultReturnUnit, proratedCredit, returnAllChoice, returnBaseQty, returnQtyError } from '../../../shared/units/return-units';

/** Purchase-line quantity error for the base-decimals rule (server wording); null when fine. */
export function purchaseBaseDecimalsError(
  qty: number | null | undefined,
  unit: Pick<PurchaseProductUnit, 'unitCode' | 'conversionFactorToBase'> | undefined,
  baseDecimalPlaces: number,
  baseLabel: string,
): string | null {
  if (!qty || !unit) return null;
  const base = convert(qty, unit.conversionFactorToBase, 1);
  if (hasAtMostDecimals(base, baseDecimalPlaces)) return null;
  return `${formatQuantity(qty, 3)} ${unit.unitCode} = ${formatQuantity(base, 3)} ${baseLabel}; ${baseLabel} is tracked to ${baseDecimalPlaces} decimal place(s).`;
}
