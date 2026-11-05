/** Mirrors `PricingRowDto` (Api/Contracts/PricingContracts.cs). One row per sellable product unit. */
export interface PricingRow {
  productId: string;
  productSku: string;
  productName: string;
  productUnitId: string;
  unitCode: string;
  unitLabel: string;
  conversionFactorToBase: number;
  baseUnitLabel: string;
  sortOrder: number;
  isBaseUnit: boolean;
  costPerUnit: number;
  currentPrice: number | null;
  /** Base price (or smallest priced row per base) x this row's factor, 2 dp. Null when nothing is priced. */
  suggestedPrice: number | null;
  marginPercent: number | null;
  taxLabel: string;
}

export interface SellingPriceHistoryEntry {
  id: string;
  price: number;
  effectiveFrom: string;
  effectiveTo: string | null;
}

/** Client-side grouping of pricing rows by product, units in hierarchy (`sortOrder`) order. */
export interface PricingProductGroup {
  productId: string;
  productSku: string;
  productName: string;
  rows: PricingRow[];
}
