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

const HSN_LINE_KEY = /^lines\[(\d+)\]\.hsncode$/i;

/** Request line index of a `lines[i].hsnCode` problem key (T11: a GST taxable product without HSN), or null for any other key. */
export function hsnLineIndex(field: string): number | null {
  const m = HSN_LINE_KEY.exec(field);
  return m ? Number(m[1]) : null;
}

/** "Item N: message" for a `lines[i].hsnCode` problem; `rows` maps a request index to its form row. Other keys keep the message. */
export function hsnLineMessage(field: string, message: string, rows?: readonly number[]): string {
  const index = hsnLineIndex(field);
  return index == null ? message : `Item ${(rows?.[index] ?? index) + 1}: ${message}`;
}

/** All messages of a problem's errors, with `lines[i].hsnCode` ones labelled by item; used for the preview error text. */
export function problemMessagesLabelled(errors: Record<string, string[]> | undefined, rows?: readonly number[]): string[] {
  return Object.entries(errors ?? {}).flatMap(([field, messages]) => messages.map((m) => hsnLineMessage(field, m, rows)));
}
