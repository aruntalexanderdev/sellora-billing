import type { PurchaseProductUnit } from '../purchases.models';

/** Units a purchase line may be entered in: active and purchasable, in hierarchy (`sortOrder`) order. */
export function purchaseUnitOptions<T extends PurchaseProductUnit>(units: readonly T[] | null | undefined): T[] {
  return (units ?? []).filter((u) => u.isActive && u.isPurchasable).sort((a, b) => a.sortOrder - b.sortOrder);
}

/** Preselected purchase unit: the default purchase unit, otherwise the base unit, otherwise the first option. */
export function defaultPurchaseUnit<T extends PurchaseProductUnit>(units: readonly T[] | null | undefined): T | undefined {
  const options = purchaseUnitOptions(units);
  return options.find((u) => u.isDefaultPurchaseUnit) ?? options.find((u) => u.isBaseUnit) ?? options[0];
}
