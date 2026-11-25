import { FormBuilder } from '@angular/forms';
import { applyPurchaseProblem, billDiscountFields, buildPurchasePreview, estimateReturnTax, warningLabel, type PurchasePreviewInput } from './purchase-tax';
import type { PurchaseLine } from './purchases.models';

const input = (over: Partial<PurchasePreviewInput> = {}): PurchasePreviewInput => ({
  supplierId: 's1', purchaseDate: '2026-11-01', ratesIncludeTax: false, billDiscountMode: 'percent', billDiscountPercent: null, billDiscountAmount: null, billCharges: 0,
  lines: [{ productId: 'p1', productUnitId: 'u1', qty: 2, rate: 50, otherCharges: 0, discount: 5 }], ...over,
});

describe('buildPurchasePreview', () => {
  it('builds the purchase preview request (lines map rate to unitPrice)', () => {
    const plan = buildPurchasePreview(input({ ratesIncludeTax: true, billCharges: 12, billDiscountPercent: 10 }))!;
    expect(plan.request).toEqual({
      direction: 'Purchase', partyId: 's1', date: '2026-11-01', ratesIncludeTax: true, billDiscountPercent: 10, billDiscountAmount: null, billCharges: 12,
      lines: [{ productId: 'p1', productUnitId: 'u1', qty: 2, unitPrice: 50, discount: 5, otherCharges: 0 }],
    });
    expect(plan.rows).toEqual([0]);
  });

  it('sends only complete rows and maps them back to the form rows', () => {
    const blank = { productId: '', productUnitId: '', qty: null, rate: null, otherCharges: 0, discount: 0 };
    const plan = buildPurchasePreview(input({ lines: [blank, input().lines[0], { ...input().lines[0], qty: 0 }, { ...input().lines[0], rate: 0 }] }))!;
    expect(plan.rows).toEqual([1, 3]);
    expect(plan.request.lines).toHaveLength(2);
  });

  it('is null without supplier, date or a complete row', () => {
    expect(buildPurchasePreview(input({ supplierId: '' }))).toBeNull();
    expect(buildPurchasePreview(input({ purchaseDate: '' }))).toBeNull();
    expect(buildPurchasePreview(input({ lines: [] }))).toBeNull();
  });
});

describe('billDiscountFields (percent OR amount)', () => {
  it('sends only the active mode, even when the other holds a value', () => {
    expect(billDiscountFields('percent', 10, 99)).toEqual({ billDiscountPercent: 10, billDiscountAmount: null });
    expect(billDiscountFields('amount', 10, 99)).toEqual({ billDiscountPercent: null, billDiscountAmount: 99 });
  });
  it('omits empty / zero values', () => {
    expect(billDiscountFields('percent', 0, null)).toEqual({ billDiscountPercent: null, billDiscountAmount: null });
    expect(billDiscountFields('amount', null, null)).toEqual({ billDiscountPercent: null, billDiscountAmount: null });
  });
});

describe('applyPurchaseProblem', () => {
  function form() {
    const fb = new FormBuilder().nonNullable;
    const line = () => fb.group({ qty: [1], rate: [1], productUnitId: [''], productId: [''], supplierBatchNumber: [''] });
    return fb.group({ supplierId: [''], billDiscountPercent: [null as number | null], billDiscountAmount: [null as number | null], billCharges: [0], lines: fb.array([line(), line()]) });
  }

  it('puts lines[i].field errors on the line control', () => {
    const f = form();
    const banner = applyPurchaseProblem(f, { errors: { 'lines[1].rate': ['Rate has too many decimals.'], 'lines[0].qty': ['Bad qty.'], 'lines[1].supplierBatchNumber': ['Too long.'] } }, 'percent');
    expect(banner).toBeNull();
    expect(f.controls.lines.at(1).controls.rate.errors?.['server']).toBe('Rate has too many decimals.');
    expect(f.controls.lines.at(0).controls.qty.errors?.['server']).toBe('Bad qty.');
    expect(f.controls.lines.at(1).controls.supplierBatchNumber.errors?.['server']).toBe('Too long.');
    expect(f.controls.lines.at(1).controls.rate.touched).toBe(true);
  });

  it('maps billDiscount to the active mode control and billCharges to its field', () => {
    const pct = form();
    applyPurchaseProblem(pct, { errors: { billDiscount: ['At most one.'], billCharges: ['Too precise.'] } }, 'percent');
    expect(pct.controls.billDiscountPercent.errors?.['server']).toBe('At most one.');
    expect(pct.controls.billDiscountAmount.errors).toBeNull();
    expect(pct.controls.billCharges.errors?.['server']).toBe('Too precise.');
    const amt = form();
    applyPurchaseProblem(amt, { errors: { BillDiscount: ['Over the cap.'] } }, 'amount');
    expect(amt.controls.billDiscountAmount.errors?.['server']).toBe('Over the cap.');
  });

  it('returns branchId, lines (rate changed during save) and unknown fields as the banner', () => {
    const f = form();
    const banner = applyPurchaseProblem(f, { errors: { branchId: ['Set the branch state.'], lines: ['A rate changed while saving.'] } }, 'percent');
    expect(banner).toBe('Set the branch state. A rate changed while saving.');
  });

  it('maps supplierId and falls back to the problem title without field errors', () => {
    const f = form();
    expect(applyPurchaseProblem(f, { errors: { supplierId: ['Unknown.'] } }, 'percent')).toBeNull();
    expect(f.controls.supplierId.errors?.['server']).toBe('Unknown.');
    expect(applyPurchaseProblem(f, { title: 'Boom' }, 'percent')).toBe('Boom');
  });
});

describe('warningLabel', () => {
  it('numbers the item for lines[i].hsnCode (through the row map) and passes others through', () => {
    expect(warningLabel('lines[1].hsnCode', 'No HSN.', [0, 3])).toBe('Item 4: No HSN.');
    expect(warningLabel('lines[0].hsnCode', 'No HSN.')).toBe('Item 1: No HSN.');
    expect(warningLabel('supplierId', 'Supplier has no state.')).toBe('Supplier has no state.');
  });
});

describe('estimateReturnTax', () => {
  const line = (taxes: PurchaseLine['taxes']) =>
    ({ baseQty: 10, taxableValue: 100, taxAmount: 18, taxRatePercent: 18, taxes }) as unknown as PurchaseLine;
  const row = (code: string, taxAmount: number, extra = {}) => ({ code, kind: 'Cgst', method: 'Percent', ratePercent: 9, amountPerBaseUnit: null, baseAmount: 100, taxAmount, originalBaseAmount: null, originalTaxAmount: null, ...extra });

  it('prorates the original rows by base quantity', () => {
    const est = estimateReturnTax(line([row('CGST', 9), row('SGST', 9)] as never), 4);
    expect(est.taxable).toBe(40);
    expect(est.tax).toBe(7.2);
    expect(est.rows.map((r) => [r.code, r.taxAmount, r.baseAmount])).toEqual([['CGST', 3.6, 40], ['SGST', 3.6, 40]]);
  });

  it('keeps a per-unit row base as a quantity', () => {
    const est = estimateReturnTax(line([row('CESS', 5, { method: 'AmountPerBaseUnit', ratePercent: null, amountPerBaseUnit: 0.5, baseAmount: 10 })] as never), 5);
    expect(est.rows[0]).toMatchObject({ baseAmount: 5, taxAmount: 2.5 });
  });

  it('handles legacy rows as one Tax row and history without rows from the line tax', () => {
    const legacy = estimateReturnTax(line([row('GST', 18, { isLegacy: true, ratePercent: 18 })] as never), 5);
    expect(legacy.rows.map((r) => r.code)).toEqual(['Tax']);
    expect(legacy.tax).toBe(9);
    const none = estimateReturnTax(line(null), 5);
    expect(none.rows).toEqual([]);
    expect(none.tax).toBe(9);
  });
});
