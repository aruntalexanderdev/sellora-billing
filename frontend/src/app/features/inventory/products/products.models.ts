export interface SellingMode {
  id: string;
  label: string;
  conversionFactorToBase: number;
  isActive: boolean;
}

export interface SellingModeInput {
  label: string;
  conversionFactorToBase: number;
}

export interface ProductListItem {
  id: string;
  sku: string;
  name: string;
  category: string | null;
  baseUnitLabel: string;
  taxLabel: string;
  reorderLevel: number;
  isActive: boolean;
}

export interface ProductDetail {
  id: string;
  sku: string;
  name: string;
  category: string | null;
  baseUnitId: string;
  baseUnitLabel: string;
  taxId: string;
  taxLabel: string;
  taxRatePercent: number;
  reorderLevel: number;
  isActive: boolean;
  sellingModes: SellingMode[];
}

export interface UpsertProductRequest {
  sku: string;
  name: string;
  category?: string | null;
  baseUnitId: string;
  taxId: string;
  reorderLevel: number;
  sellingModes: SellingModeInput[];
}
