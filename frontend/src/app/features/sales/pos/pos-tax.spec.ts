import { FormArray, FormControl, FormGroup } from '@angular/forms';
import { applySaleProblem, buildSalePreview, problemField, totalChangedMessage, type SalePreviewInput } from './pos-tax';

const input = (over: Partial<SalePreviewInput> = {}): SalePreviewInput => ({
  customerId: 'c1', invoiceDate: '2026-11-25', canBillDiscount: true, billDiscountMode: 'percent', billDiscountPercent: null, billDiscountAmount: null, billCharges: 0,
  lines: [{ productId: 'p', productUnitId: 'u', qty: 2, unitPrice: 50, discount: 0 }], ...over,
});

describe('buildSalePreview', () => {
  it('builds the Sale request and maps request rows back to cart rows (incomplete rows are skipped)', () => {
    const plan = buildSalePreview(input({ lines: [{ productId: 'p', productUnitId: 'u', qty: 0, unitPrice: 5, discount: 0 }, { productId: 'p', productUnitId: 'u', qty: 2, unitPrice: 50, discount: 1 }] }))!;
    expect(plan.rows).toEqual([1]);
    expect(plan.request).toEqual({
      direction: 'Sale', partyId: 'c1', date: '2026-11-25', billDiscountPercent: null, billDiscountAmount: null, billCharges: 0,
      lines: [{ productId: 'p', productUnitId: 'u', qty: 2, unitPrice: 50, discount: 1 }],
    });
  });

  it('is null without a customer, a date or a complete row', () => {
    expect(buildSalePreview(input({ customerId: '' }))).toBeNull();
    expect(buildSalePreview(input({ invoiceDate: '' }))).toBeNull();
    expect(buildSalePreview(input({ lines: [] }))).toBeNull();
  });

  it('sends only the active bill discount mode, and none without the permission', () => {
    expect(buildSalePreview(input({ billDiscountPercent: 10, billDiscountAmount: 5 }))!.request).toMatchObject({ billDiscountPercent: 10, billDiscountAmount: null });
    expect(buildSalePreview(input({ billDiscountMode: 'amount', billDiscountPercent: 10, billDiscountAmount: 5 }))!.request).toMatchObject({ billDiscountPercent: null, billDiscountAmount: 5 });
    expect(buildSalePreview(input({ canBillDiscount: false, billDiscountPercent: 10 }))!.request).toMatchObject({ billDiscountPercent: null, billDiscountAmount: null });
  });
});

describe('applySaleProblem', () => {
  const form = () =>
    new FormGroup({
      customerId: new FormControl(''),
      billCharges: new FormControl(0),
      billDiscountPercent: new FormControl<number | null>(null),
      lines: new FormArray([new FormGroup({ qty: new FormControl(1), unitPrice: new FormControl(1) })]),
    });

  it('maps lines[i].field and named fields onto controls; lines / expectedGrandTotal / branchId become the banner', () => {
    const f = form();
    const banner = applySaleProblem(f, {
      title: 'Validation', status: 400, traceId: 't1',
      errors: { 'lines[0].qty': ['Bad qty.'], customerId: ['Bad customer.'], billDiscountPercent: ['Over cap.'], lines: ['Only 3 in stock.'], branchId: ['Pick a branch.'] },
    } as never);
    expect(f.get('lines.0.qty')?.errors?.['server']).toBe('Bad qty.');
    expect(f.get('customerId')?.errors?.['server']).toBe('Bad customer.');
    expect(f.get('billDiscountPercent')?.errors?.['server']).toBe('Over cap.');
    expect(banner).toBe('Only 3 in stock. Pick a branch. (Ref: t1)');
  });

  it('returns null when every error sits on a control, and the problem message without field errors', () => {
    expect(applySaleProblem(form(), { status: 400, errors: { billCharges: ['x'] } } as never)).toBeNull();
    expect(applySaleProblem(form(), { status: 500, title: 'Boom' } as never)).toBe('Boom');
  });

  it('reads one field of a problem case-insensitively', () => {
    const p = { status: 400, errors: { ExpectedGrandTotal: ['The total is now 12.00.'] } } as never;
    expect(totalChangedMessage(p)).toBe('The total is now 12.00.');
    expect(problemField(p, 'billCharges')).toBeNull();
  });
});
