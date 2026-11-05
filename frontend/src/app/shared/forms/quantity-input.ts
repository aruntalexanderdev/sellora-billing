import { AbstractControl, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';

/** Clamp a unit's `decimalPlaces` to the supported 0–3 range (Units master rule). */
function clampDecimals(decimalPlaces: number | null | undefined): number {
  const dp = Math.trunc(decimalPlaces ?? 0);
  return Math.min(3, Math.max(0, Number.isFinite(dp) ? dp : 0));
}

/** The HTML `step` for a quantity input in a unit with the given decimal places: 0 → "1", 2 → "0.01". */
export function quantityStep(decimalPlaces: number | null | undefined): string {
  const dp = clampDecimals(decimalPlaces);
  return dp === 0 ? '1' : `0.${'0'.repeat(dp - 1)}1`;
}

/** Smallest positive quantity enterable in a unit with the given decimal places. */
export function minQuantity(decimalPlaces: number | null | undefined): number {
  return Number(quantityStep(decimalPlaces));
}

/** Count decimals of a finite number as typed (handles exponent notation such as 1e-7). */
function countDecimals(value: number): number {
  const text = value.toString().toLowerCase();
  const [mantissa, exponent] = text.split('e');
  const fraction = mantissa.split('.')[1]?.length ?? 0;
  return Math.max(0, fraction - Number(exponent ?? 0));
}

/**
 * Rejects values with more decimals than the unit allows: `{ maxDecimalPlaces: { max, actual } }`.
 * Empty values pass (combine with `Validators.required`). Mirrors the server's entered-quantity check.
 */
export function maxDecimalPlaces(decimalPlaces: number | null | undefined): ValidatorFn {
  const max = clampDecimals(decimalPlaces);
  return (control: AbstractControl): ValidationErrors | null => {
    const value = control.value;
    if (value === null || value === undefined || value === '') return null;
    const num = Number(value);
    if (!Number.isFinite(num)) return null;
    const actual = countDecimals(num);
    return actual > max ? { maxDecimalPlaces: { max, actual } } : null;
  };
}

/** Standard validators for a required, positive quantity entered in a unit with the given decimal places. */
export function quantityValidators(decimalPlaces: number | null | undefined): ValidatorFn[] {
  return [Validators.required, Validators.min(minQuantity(decimalPlaces)), maxDecimalPlaces(decimalPlaces)];
}
