import { AbstractControl, ValidationErrors } from '@angular/forms';

/** Base currency decimal places. The app has no currency setting yet; mirrors the server default. */
export const BASE_CURRENCY_DECIMALS = 2;

/**
 * Rounds a money value to `decimals` places, half away from zero (the server's MidpointRounding.AwayFromZero).
 * The scaled value is first normalised to 15 significant digits so binary drift (1.005 * 100 = 100.49999999999999)
 * does not flip a decimal midpoint.
 */
export function roundMoney(value: number, decimals: number = BASE_CURRENCY_DECIMALS): number {
  if (!Number.isFinite(value)) return 0;
  const scale = 10 ** decimals;
  const scaled = Number((Math.abs(value) * scale).toPrecision(15));
  const rounded = Math.round(scaled) / scale; // Math.round on a positive value is half-up == away from zero
  return value < 0 && rounded !== 0 ? -rounded : rounded;
}

/** Gross amount of a line (qty x unit price), rounded like the server. */
export function grossAmount(qty: number | null | undefined, unitPrice: number | null | undefined, decimals: number = BASE_CURRENCY_DECIMALS): number {
  return roundMoney((qty ?? 0) * (unitPrice ?? 0), decimals);
}

/** Per-line rounded values: taxable, tax and total each rounded to the currency precision. */
export function lineAmounts(
  qty: number | null | undefined,
  unitPrice: number | null | undefined,
  discount: number | null | undefined,
  taxRatePercent: number,
  decimals: number = BASE_CURRENCY_DECIMALS,
): { gross: number; taxable: number; tax: number; total: number } {
  const gross = grossAmount(qty, unitPrice, decimals);
  const taxable = roundMoney((qty ?? 0) * (unitPrice ?? 0) - (discount ?? 0), decimals);
  const tax = roundMoney((taxable * taxRatePercent) / 100, decimals);
  return { gross, taxable, tax, total: roundMoney(taxable + tax, decimals) };
}

/** True when a discount amount is more than the line's gross (qty x unit price). */
export function discountExceedsGross(
  qty: number | null | undefined,
  unitPrice: number | null | undefined,
  discount: number | null | undefined,
  decimals: number = BASE_CURRENCY_DECIMALS,
): boolean {
  // Same comparison as the server (SalesService): the discount against the UNROUNDED qty x price (tiny epsilon for float noise).
  void decimals;
  return (discount ?? 0) > (qty ?? 0) * (unitPrice ?? 0) + 1e-9;
}

/** True when `value` has more decimals than the currency allows (the server rejects such discounts with 400). */
export function hasTooManyDecimals(value: number | null | undefined, decimals: number = BASE_CURRENCY_DECIMALS): boolean {
  if (typeof value !== 'number' || !Number.isFinite(value)) return false;
  return Math.abs(roundMoney(value, decimals) - value) > 1e-9;
}

/** Valid percentage entry: a number from 0 to 100 inclusive. */
export function isValidPercent(percent: number | null | undefined): boolean {
  return typeof percent === 'number' && Number.isFinite(percent) && percent >= 0 && percent <= 100;
}

/** Discount amount for a percentage of the gross: rounded to the currency precision, never above the gross; 0 for an invalid percent. */
export function discountFromPercent(
  qty: number | null | undefined,
  unitPrice: number | null | undefined,
  percent: number | null | undefined,
  decimals: number = BASE_CURRENCY_DECIMALS,
): number {
  if (!isValidPercent(percent)) return 0;
  const gross = grossAmount(qty, unitPrice, decimals);
  return Math.min(gross, roundMoney((gross * (percent as number)) / 100, decimals));
}

/** Line-group validator: at most the currency's decimals -> `{ discountDecimals: true }`; not above `qty * unitPrice` -> `{ discountExceedsGross: true }`. */
export function discountWithinGross(group: AbstractControl): ValidationErrors | null {
  const v = (group as { value?: { qty?: number; unitPrice?: number; discount?: number } }).value ?? {};
  if (hasTooManyDecimals(v.discount)) return { discountDecimals: true };
  return discountExceedsGross(v.qty, v.unitPrice, v.discount) ? { discountExceedsGross: true } : null;
}
