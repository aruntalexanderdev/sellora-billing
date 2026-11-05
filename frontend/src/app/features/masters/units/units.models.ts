/** Mirrors backend `MeasurementCategory` (string enum). */
export type MeasurementCategory = 'Count' | 'Weight' | 'Volume' | 'Length' | 'Packaging';

/** Categories that carry a physical measure (fractional decimals + optional standard factor). */
export type MeasurableCategory = Extract<MeasurementCategory, 'Weight' | 'Volume' | 'Length'>;

export const MEASUREMENT_CATEGORIES: readonly MeasurementCategory[] = ['Count', 'Weight', 'Volume', 'Length', 'Packaging'];

export const MEASURABLE_CATEGORIES: readonly MeasurableCategory[] = ['Weight', 'Volume', 'Length'];

/** Reference unit that `standardFactor` is expressed against, per measurable category. */
export const REFERENCE_UNIT: Record<MeasurableCategory, string> = { Weight: 'kg', Volume: 'L', Length: 'm' };

export function isMeasurableCategory(category: MeasurementCategory): category is MeasurableCategory {
  return (MEASURABLE_CATEGORIES as readonly MeasurementCategory[]).includes(category);
}

/** Mirrors backend `UnitDto`. */
export interface UnitItem {
  id: string;
  name: string;
  shortCode: string;
  measurementCategory: MeasurementCategory;
  decimalPlaces: number;
  standardFactor: number | null;
  isActive: boolean;
  /**
   * True when the unit is referenced by any product, as its base unit or by any product unit row
   * (active or inactive). Category, decimal places and standard factor are then frozen.
   * Drives the unit-form locks only; deactivation is governed by `canDeactivate`.
   */
  isInUse: boolean;
  /** False when the unit is a product base unit or used by an active product unit (server rejects deactivation). */
  canDeactivate: boolean;
}

/** Mirrors backend `UpsertUnitRequest`. */
export interface UpsertUnitRequest {
  name: string;
  shortCode: string;
  measurementCategory: MeasurementCategory;
  decimalPlaces: number;
  standardFactor: number | null;
}
