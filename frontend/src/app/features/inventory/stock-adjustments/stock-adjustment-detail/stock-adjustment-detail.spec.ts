import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { StockAdjustmentDetail } from './stock-adjustment-detail';
import { StockAdjustmentsService } from '../stock-adjustments.service';
import type { StockAdjustmentDetail as Detail } from '../stock-adjustments.models';

function detail(overrides: Partial<Detail> = {}): Detail {
  return {
    id: 'adj-1', adjustmentNo: 'ADJ-0001', createdAt: '2026-11-06T10:00:00Z', productId: 'pen', productName: 'Ball Pen', sku: 'STN-PEN',
    type: 'OpeningStock', direction: 'Increase', enteredQuantity: 3, productUnitId: 'pu-fctn', unitCode: 'fctn', unitDisplayName: 'Full Carton',
    conversionFactorToBase: 100, enteredDecimalPlaces: 0, quantityBaseUnits: 300, baseUnitCode: 'pc', baseDecimalPlaces: 0,
    enteredUnitCost: 250, unitCost: 2.5, reason: 'First count', beforeQty: 0, afterQty: 300, userName: 'Ravi Krishnan',
    batches: [{ purchaseBatchId: 'b1', internalBatchNo: 'BATCH-0001', quantityBaseUnits: 300, unitCost: 2.5 }],
    ...overrides,
  };
}

function setup(response: Observable<Detail>) {
  const get = vi.fn(() => response);
  TestBed.configureTestingModule({
    imports: [StockAdjustmentDetail],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'adj-1' }) } } },
      { provide: StockAdjustmentsService, useValue: { get } },
    ],
  });
  const fixture = TestBed.createComponent(StockAdjustmentDetail);
  fixture.detectChanges();
  return { el: fixture.nativeElement as HTMLElement, get };
}

const text = (el: HTMLElement, selector: string) => (el.querySelector(selector)?.textContent ?? '').replace(/\s+/g, ' ').trim();

describe('StockAdjustmentDetail', () => {
  it('loads the adjustment from the route id and shows entered vs base quantity with the posting-time factor', () => {
    const { el, get } = setup(of(detail()));

    expect(get).toHaveBeenCalledWith('adj-1');
    expect(text(el, '[data-testid="entered-qty"]')).toBe('+3 fctn');
    expect(text(el, '[data-testid="base-qty"]')).toBe('+300 pc');
    expect(el.textContent).toContain('Full Carton');
    expect(el.textContent).toContain('1 fctn = 100 pc (at posting)');
    expect(el.textContent).toContain('0 → 300 pc');
  });

  it('shows the cost per entered unit and per base unit, and the batch it created', () => {
    const { el } = setup(of(detail()));
    const page = (el.textContent ?? '').replace(/\s+/g, ' ');

    expect(page).toContain('₹250.00 / fctn');
    expect(page).toContain('= ₹2.50 / pc');
    const row = text(el, 'tbody tr');
    expect(row).toContain('BATCH-0001');
    expect(row).toContain('+300');
  });

  it('formats a fractional-kg decrease with the unit precision, negative batch draws and no cost card', () => {
    const { el } = setup(of(detail({
      type: 'Damage', direction: 'Decrease', enteredQuantity: 1.25, unitCode: 'kg', unitDisplayName: 'Loose kg', conversionFactorToBase: 1,
      enteredDecimalPlaces: 3, quantityBaseUnits: 1.25, baseUnitCode: 'kg', baseDecimalPlaces: 3, enteredUnitCost: null, unitCost: null,
      beforeQty: 10, afterQty: 8.75, reason: null,
      batches: [
        { purchaseBatchId: 'b1', internalBatchNo: 'BATCH-0001', quantityBaseUnits: -1, unitCost: 58 },
        { purchaseBatchId: 'b2', internalBatchNo: 'BATCH-0002', quantityBaseUnits: -0.25, unitCost: 62.3333 },
      ],
    })));

    expect(text(el, '[data-testid="entered-qty"]')).toBe('−1.250 kg');
    expect(text(el, '[data-testid="base-qty"]')).toBe('−1.250 kg');
    expect(el.textContent).toContain('10.000 → 8.750 kg');
    expect(el.textContent).not.toContain('Cost');
    const rows = Array.from(el.querySelectorAll('tbody tr')).map((r) => (r.textContent ?? '').replace(/\s+/g, ' '));
    expect(rows).toHaveLength(2);
    expect(rows[0]).toContain('-1.000');
    expect(rows[1]).toContain('-0.250');
    expect(rows[1]).toContain('₹62.3333');
    expect(el.querySelectorAll('td.negative')).toHaveLength(2);
  });

  it('shows "not found" when the request fails', () => {
    const { el } = setup(throwError(() => new Error('404')));

    expect(el.textContent).toContain('Adjustment not found.');
  });
});
