import type { SaleCatalogUnit } from '../sales.models';

/** A unit can be sold at the POS only when it has a list price (D2: unpriced units are refused). */
export function isPriced(unit: Pick<SaleCatalogUnit, 'currentPrice'> | null | undefined): boolean {
  return !!unit && unit.currentPrice !== null && unit.currentPrice !== undefined;
}

/**
 * Preselected POS unit for a catalog product: the default sales unit when it is priced, otherwise the
 * first priced unit; undefined when nothing is priced. The catalog lists only active, sellable units.
 */
export function defaultSaleUnit(units: readonly SaleCatalogUnit[] | null | undefined): SaleCatalogUnit | undefined {
  const priced = (units ?? []).filter(isPriced);
  return priced.find((u) => u.isDefault) ?? priced[0];
}

const to4dp = (n: number) => Math.round((n + Number.EPSILON) * 1e4) / 1e4;

/** True when the charged price differs from the list price (compared at the server's 4 dp). */
export function priceDiffers(price: number | null | undefined, listPrice: number | null | undefined): boolean {
  if (price == null || listPrice == null) return false;
  return to4dp(price) !== to4dp(listPrice);
}

/**
 * Whether a line needs the `Sales.PriceOverride` permission: its price differs from the list price, or it carries a
 * discount. Such lines are sent with `overridePrice: true`.
 */
export function needsPriceOverride(
  price: number | null | undefined,
  listPrice: number | null | undefined,
  discount: number | null | undefined,
): boolean {
  return priceDiffers(price, listPrice) || (discount ?? 0) > 0;
}
