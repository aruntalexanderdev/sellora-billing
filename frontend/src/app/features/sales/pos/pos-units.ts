import type { SaleCatalogUnit } from '../sales.models';

/**
 * Preselected POS unit for a catalog product: the default sales unit, otherwise the first priced unit,
 * otherwise the first unit. The catalog already lists only active, sellable units in hierarchy order.
 */
export function defaultSaleUnit(units: readonly SaleCatalogUnit[] | null | undefined): SaleCatalogUnit | undefined {
  const list = units ?? [];
  return list.find((u) => u.isDefault) ?? list.find((u) => u.currentPrice !== null) ?? list[0];
}
