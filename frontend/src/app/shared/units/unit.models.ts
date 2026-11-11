/** Mirrors the backend BreakdownUnit record: one unit usable to express a base quantity as packaging. */
export interface BreakdownUnit {
  productUnitId: string;
  unitCode: string;
  label: string;
  factor: number;
  isBaseUnit: boolean;
}
