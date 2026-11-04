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
  /** True when at least one product uses this unit as its base unit. */
  isInUse: boolean;
}

/** Mirrors backend `UpsertUnitRequest`. */
export interface UpsertUnitRequest {
  name: string;
  shortCode: string;
  measurementCategory: MeasurementCategory;
  decimalPlaces: number;
  standardFactor: number | null;
}
