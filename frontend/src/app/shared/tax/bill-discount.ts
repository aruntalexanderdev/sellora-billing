/** The bill discount is entered either as a percent or as an amount, never both. */
export type BillDiscountMode = 'percent' | 'amount';

/** The bill discount fields of a request: at most one is non-null (the active mode, when it holds a positive value). */
export function billDiscountFields(
  mode: BillDiscountMode,
  percent: number | null | undefined,
  amount: number | null | undefined,
): { billDiscountPercent: number | null; billDiscountAmount: number | null } {
  const positive = (n: number | null | undefined) => (typeof n === 'number' && Number.isFinite(n) && n > 0 ? n : null);
  return mode === 'percent'
    ? { billDiscountPercent: positive(percent), billDiscountAmount: null }
    : { billDiscountPercent: null, billDiscountAmount: positive(amount) };
}
