import type { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

/** HSN/SAC code: empty = none, otherwise 4, 6 or 8 digits (mirrors backend `TaxSupport.NormaliseHsn`, BR9). */
export const HSN_PATTERN = /^(\d{4}|\d{6}|\d{8})$/;
export const HSN_MESSAGE = 'The HSN/SAC code must be 4, 6 or 8 digits.';

export function isValidHsn(value: string | null | undefined): boolean {
  const v = (value ?? '').trim();
  return v === '' || HSN_PATTERN.test(v);
}

export const hsnValidator: ValidatorFn = (control: AbstractControl): ValidationErrors | null =>
  isValidHsn(control.value as string | null) ? null : { hsn: HSN_MESSAGE };

/** Trimmed code or null when empty. */
export function hsnOrNull(value: string | null | undefined): string | null {
  const v = (value ?? '').trim();
  return v === '' ? null : v;
}
