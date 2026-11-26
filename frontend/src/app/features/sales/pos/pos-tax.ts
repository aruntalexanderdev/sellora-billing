import type { FormGroup } from '@angular/forms';
import { applyProblem } from '../../../core/http/api-errors';
import type { ApiProblem } from '../../../core/auth/auth.models';
import { hsnLineIndex, hsnLineMessage } from '../../../shared/tax/hsn';
import { billDiscountFields, type BillDiscountMode } from '../../../shared/tax/bill-discount';
import type { PreviewPlan } from '../../../shared/tax/tax-preview.service';

export interface SalePreviewInput {
  customerId: string;
  invoiceDate: string;
  /** Only holders of Sales.BillDiscount send one; for everyone else it is left out of the request. */
  canBillDiscount: boolean;
  billDiscountMode: BillDiscountMode;
  billDiscountPercent: number | null;
  billDiscountAmount: number | null;
  billCharges: number | null;
  lines: { productId: string; productUnitId: string; qty: number | null; unitPrice: number | null; discount: number | null }[];
}

/**
 * The `/api/v1/tax/preview` request for the POS cart. Only complete rows (product, unit, qty > 0, price) are sent;
 * `rows` maps the response `index` back to the cart row. `null` while there is no customer, date or complete row.
 * Sales have no per-line other charges.
 */
export function buildSalePreview(input: SalePreviewInput): PreviewPlan | null {
  if (!input.customerId || !input.invoiceDate) return null;
  const rows: number[] = [];
  const lines = input.lines.flatMap((l, i) => {
    if (!l.productId || !l.productUnitId || !l.qty || l.qty <= 0 || l.unitPrice == null) return [];
    rows.push(i);
    return [{ productId: l.productId, productUnitId: l.productUnitId, qty: l.qty, unitPrice: l.unitPrice, discount: l.discount ?? 0 }];
  });
  if (lines.length === 0) return null;
  const bill = input.canBillDiscount
    ? billDiscountFields(input.billDiscountMode, input.billDiscountPercent, input.billDiscountAmount)
    : { billDiscountPercent: null, billDiscountAmount: null };
  return {
    rows,
    request: { direction: 'Sale', partyId: input.customerId, date: input.invoiceDate, ...bill, billCharges: input.billCharges ?? 0, lines },
  };
}

/** Save/preview error keys that have no control: they come back as the banner message. */
const BANNER_KEYS = new Set(['lines', 'branchid', 'expectedgrandtotal', 'stocklocationid']);

/**
 * Maps a sale ProblemDetails onto the POS form: `lines[i].qty|unitPrice|discount|productUnitId|productId` land on the cart row,
 * `customerId`, `billDiscountPercent`, `billDiscountAmount` and `billCharges` on their controls. `lines` (empty cart, stock,
 * FIFO, rate changed), `branchId`, `expectedGrandTotal` and anything unknown come back as the banner message (null when none).
 */
export function applySaleProblem(form: FormGroup, problem: ApiProblem | null | undefined): string | null {
  const errors = Object.entries(problem?.errors ?? {});
  if (errors.length === 0) return applyProblem(form, problem);
  const banner: string[] = [];
  const forControls: Record<string, string[]> = {};
  for (const [field, messages] of errors) {
    if (hsnLineIndex(field) != null) {
      // T11: a GST taxable product without HSN. Names the cart row and marks its product control.
      banner.push(hsnLineMessage(field, messages.join(' ')));
      const control = form.get(['lines', String(hsnLineIndex(field)), 'productId']);
      control?.setErrors({ ...(control.errors ?? {}), server: messages.join(' ') });
      control?.markAsTouched();
    } else if (BANNER_KEYS.has(field.toLowerCase())) banner.push(messages.join(' '));
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

/** The first server message of a field of a (preview) problem, or null. */
export function problemField(problem: ApiProblem | null | undefined, field: string): string | null {
  const hit = Object.entries(problem?.errors ?? {}).find(([k]) => k.toLowerCase() === field.toLowerCase());
  return hit ? hit[1].join(' ') : null;
}

/** Server message of the `expectedGrandTotal` field (the sale total moved while the cashier was looking at it), or null. */
export const totalChangedMessage = (problem: ApiProblem | null | undefined): string | null => problemField(problem, 'expectedGrandTotal');
