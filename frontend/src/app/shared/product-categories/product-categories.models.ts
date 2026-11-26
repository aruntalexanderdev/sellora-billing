import type { FieldWarning } from '../tax/save-warnings';

/** Mirrors backend `ProductSubcategoryDto`. */
export interface ProductSubcategory {
  id: string;
  categoryId: string;
  code: string;
  name: string;
  sortOrder: number;
  isActive: boolean;
  productCount: number;
  /** Default HSN/SAC code of the subcategory's products (BR9). */
  hsnCode?: string | null;
  /** Update response only (T11): GST-taxable products left without an HSN. The save is never blocked. */
  warnings?: FieldWarning[] | null;
}

/** Mirrors backend `ProductCategoryDto`. */
export interface ProductCategory {
  id: string;
  code: string;
  name: string;
  sortOrder: number;
  isActive: boolean;
  productCount: number;
  subcategories: ProductSubcategory[];
}

/** Mirrors backend category/subcategory upsert request. */
export interface UpsertClassificationRequest {
  code: string;
  name: string;
  sortOrder: number;
  /** Subcategories only: 4/6/8 digits, null/empty = none. */
  hsnCode?: string | null;
}

/** Value emitted by `app-category-filter`. */
export interface CategorySelection {
  categoryId: string | null;
  subcategoryId: string | null;
}

export const CODE_PATTERN = /^[A-Z0-9][A-Z0-9-]{0,19}$/;
export const CODE_MAX_LENGTH = 20;

/** Suggests a code from a name: upper-case, runs of non-alphanumerics become '-', trimmed of '-', max 20 chars. */
export function suggestCode(name: string): string {
  return name
    .toUpperCase()
    .replace(/[^A-Z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, CODE_MAX_LENGTH)
    .replace(/-+$/g, '');
}
