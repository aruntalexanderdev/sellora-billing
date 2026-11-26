import type { FormGroup } from '@angular/forms';
import { applyProblem } from '../../core/http/api-errors';
import type { ApiProblem } from '../../core/auth/auth.models';
import { roundMoney } from '../../shared/money/money';
import { displayRows } from '../../shared/tax/tax-breakdown';
import type { PreviewPlan } from '../../shared/tax/tax-preview.service';
import type { TaxRowView } from '../../shared/tax/tax-preview.models';
import { proratedCredit } from '../../shared/units/return-units';
import type { PurchaseLine, PurchaseLineTax } from './purchases.models';

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

export interface PurchasePreviewInput {
  supplierId: string;
  purchaseDate: string;
  ratesIncludeTax: boolean;
  billDiscountMode: BillDiscountMode;
  billDiscountPercent: number | null;
  billDiscountAmount: number | null;
  billCharges: number | null;
  lines: {
    productId: string;
    productUnitId: string;
    qty: number | null;
    rate: number | null;
    otherCharges: number | null;
    discount: number | null;
  }[];
}

/**
 * The `/api/v1/tax/preview` request for a purchase form. Only complete rows (product, unit, qty > 0, rate) are
 * sent; `rows` maps the response `index` back to the form row. `null` while there is no supplier, date or complete row.
 */
export function buildPurchasePreview(input: PurchasePreviewInput): PreviewPlan | null {
  if (!input.supplierId || !input.purchaseDate) return null;
  const rows: number[] = [];
  const lines = input.lines.flatMap((l, i) => {
    if (!l.productId || !l.productUnitId || !l.qty || l.qty <= 0 || l.rate == null) return [];
    rows.push(i);
    return [{ productId: l.productId, productUnitId: l.productUnitId, qty: l.qty, unitPrice: l.rate, discount: l.discount ?? 0, otherCharges: l.otherCharges ?? 0 }];
  });
  if (lines.length === 0) return null;
  const bill = billDiscountFields(input.billDiscountMode, input.billDiscountPercent, input.billDiscountAmount);
  return {
    rows,
    request: {
      direction: 'Purchase',
      partyId: input.supplierId,
      date: input.purchaseDate,
      ratesIncludeTax: input.ratesIncludeTax,
      ...bill,
      billCharges: input.billCharges ?? 0,
      lines,
    },
  };
}

/**
 * Maps a save/preview ProblemDetails onto the purchase form: `lines[i].rate|qty|productUnitId|productId|supplierBatchNumber`
 * land on the line control, `billDiscount` on the active bill discount control, `billCharges` and `supplierId` on theirs.
 * `branchId`, `lines` (rate changed during save) and anything unknown come back as the banner message.
 */
export function applyPurchaseProblem(form: FormGroup, problem: ApiProblem | null | undefined, mode: BillDiscountMode): string | null {
  const errors = Object.entries(problem?.errors ?? {});
  if (errors.length === 0) return applyProblem(form, problem);
  const control: Record<string, string> = {
    billdiscount: mode === 'percent' ? 'billDiscountPercent' : 'billDiscountAmount',
    billcharges: 'billCharges',
  };
  const banner: string[] = [];
  const forControls: Record<string, string[]> = {};
  for (const [field, messages] of errors) {
    const key = field.toLowerCase();
    if (key === 'branchid' || key === 'lines') banner.push(messages.join(' '));
    else if (control[key]) forControls[control[key]] = messages;
    else forControls[field] = messages;
  }
  if (Object.keys(forControls).length > 0) {
    const rest = applyProblem(form, { ...problem, errors: forControls });
    if (rest) banner.push(rest);
  }
  if (banner.length === 0) return null;
  const text = banner.join(' ');
  return problem?.traceId && !text.includes(problem.traceId) ? `${text} (Ref: ${problem.traceId})` : text;
}

/** Warning text for a `lines[i].hsnCode` / `supplierId` field, prefixed with the item number (`rows` maps a request index to its form row). */
export function warningLabel(field: string, message: string, rows?: readonly number[]): string {
  const m = /^lines\[(\d+)\]\.hsncode$/i.exec(field);
  if (!m) return message;
  const index = Number(m[1]);
  return `Item ${(rows?.[index] ?? index) + 1}: ${message}`;
}

export interface ReturnTaxEstimate {
  taxable: number;
  tax: number;
  rows: TaxRowView[];
}

/**
 * Estimated tax of returning `returnBaseQty` of a purchase line: the original line's rows prorated by base quantity
 * (legacy rows as one "Tax" row). The server reverses the rows exactly on save and is authoritative.
 */
export function estimateReturnTax(line: PurchaseLine, returnBaseQty: number, places = 2): ReturnTaxEstimate {
  const share = (n: number) => roundMoney(proratedCredit(n, returnBaseQty, line.baseQty), places);
  const source: readonly PurchaseLineTax[] = line.taxes && line.taxes.length > 0 ? line.taxes : [];
  const rows = displayRows(source).map((r) => ({
    ...r,
    baseAmount: r.method === 'AmountPerBaseUnit' ? roundMoney(proratedCredit(r.baseAmount, returnBaseQty, line.baseQty), 3) : share(r.baseAmount),
    taxAmount: share(r.taxAmount),
  }));
  return { taxable: share(line.taxableValue), tax: source.length > 0 ? rows.reduce((s, r) => s + r.taxAmount, 0) : share(line.taxAmount), rows };
}
