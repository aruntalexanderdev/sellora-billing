export interface PricingRow {
  productId: string;
  productSku: string;
  productName: string;
  sellingModeId: string;
  sellingModeLabel: string;
  conversionFactorToBase: number;
  baseUnitLabel: string;
  costPerUnit: number;
  currentPrice: number | null;
  marginPercent: number | null;
  taxLabel: string;
}

export interface SellingPriceHistoryEntry {
  id: string;
  price: number;
  effectiveFrom: string;
  effectiveTo: string | null;
}
