import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { StockAdjustmentList } from './stock-adjustment-list';
import { StockAdjustmentsService, stockAdjustmentListParams } from '../stock-adjustments.service';
import { ProductsService } from '../../products/products.service';
import { AuthService } from '../../../../core/auth/auth.service';
import type { StockAdjustmentListItem } from '../stock-adjustments.models';

function item(overrides: Partial<StockAdjustmentListItem> = {}): StockAdjustmentListItem {
  return {
    id: 'adj-1', adjustmentNo: 'ADJ-0001', createdAt: '2026-11-06T10:00:00Z', productId: 'pen', productName: 'Pen', sku: 'PEN-1',
    type: 'OpeningStock', direction: 'Increase', enteredQuantity: 3, unitCode: 'fctn', enteredDecimalPlaces: 0,
    quantityBaseUnits: 300, baseUnitCode: 'pc', baseDecimalPlaces: 0, enteredUnitCost: 250, unitCost: 2.5,
    beforeQty: 0, afterQty: 300, userName: 'admin',
    ...overrides,
  };
}

function setup(queryParams: Record<string, string> = {}, permissions: string[] = ['Inventory.View']) {
  const list = vi.fn(() => of({ items: [item(), item({ id: 'adj-2', adjustmentNo: 'ADJ-0002', type: 'Damage', direction: 'Decrease',
    enteredQuantity: 1.25, unitCode: 'kg', enteredDecimalPlaces: 3, quantityBaseUnits: 1.25, baseUnitCode: 'kg', baseDecimalPlaces: 3,
    enteredUnitCost: null, unitCost: null, beforeQty: 10, afterQty: 8.75 })], total: 2, page: 1, pageSize: 20 }));
  TestBed.configureTestingModule({
    imports: [StockAdjustmentList],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } } },
      { provide: StockAdjustmentsService, useValue: { list } },
      { provide: ProductsService, useValue: { list: () => of({ items: [{ id: 'pen', sku: 'PEN-1', name: 'Pen', isActive: true }], total: 1, page: 1, pageSize: 100 }) } },
      { provide: AuthService, useValue: { hasPermission: (code: string) => permissions.includes(code) } },
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(StockAdjustmentList);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, list, navigate };
}

const lastQuery = (list: ReturnType<typeof setup>['list']) => (list.mock.calls.at(-1) as unknown as [Record<string, unknown>])[0];

describe('StockAdjustmentList', () => {
  it('renders entered and base quantities with each unit precision', () => {
    const { el } = setup();
    const rows = Array.from(el.querySelectorAll('tbody tr')).map((r) => r.textContent ?? '');

    expect(rows[0]).toContain('3 fctn');
    expect(rows[0]).toContain('300 pc');
    expect(rows[0]).toContain('₹250.00 / fctn');
    expect(rows[0]).toContain('0 → 300 pc');
    expect(rows[1]).toContain('1.250 kg');
    expect(rows[1]).toContain('10.000 → 8.750 kg');
  });

  it('turns filters into query params and resets to page 1', () => {
    const { component, list } = setup();

    component.onPageChange(2);
    expect(lastQuery(list)['page']).toBe(2);

    component.onTypeChange('Damage');
    component.onDirectionChange('Decrease');
    component.onProductChange('pen');
    component.onDateFromChange('2026-11-01');
    component.onDateToChange('2026-11-06');
    component.onSearch('ADJ');

    const q = lastQuery(list);
    expect(q).toEqual({
      search: 'ADJ', productId: 'pen', type: 'Damage', direction: 'Decrease',
      dateFrom: '2026-11-01', dateTo: '2026-11-06', page: 1, pageSize: 20,
    });
    expect(stockAdjustmentListParams(q)).toEqual({
      search: 'ADJ', productId: 'pen', type: 'Damage', direction: 'Decrease',
      dateFrom: '2026-11-01', dateTo: '2026-11-06', page: '1', pageSize: '20',
    });
  });

  it('omits "All" filters from the query', () => {
    const { component, list } = setup();

    component.onTypeChange('Damage');
    component.onTypeChange('All types');
    const q = lastQuery(list);

    expect(q['type']).toBeUndefined();
    expect(q['direction']).toBeUndefined();
    expect(stockAdjustmentListParams(q)).toEqual({ page: '1', pageSize: '20' });
  });

  it('applies a productId deep link', () => {
    const { list } = setup({ productId: 'pen' });

    expect(lastQuery(list)['productId']).toBe('pen');
  });

  it('navigates to the detail from the adjustment number', () => {
    const { el, navigate } = setup();

    (el.querySelector('tbody tr .dt-cell-link') as HTMLButtonElement).click();

    expect(navigate).toHaveBeenCalledWith(['/inventory/stock-adjustments', 'adj-1']);
  });

  it('shows "New adjustment" only with Inventory.Adjust', () => {
    expect(setup().el.querySelector('.page-head')?.textContent).not.toContain('New adjustment');
    TestBed.resetTestingModule();
    expect(setup({}, ['Inventory.View', 'Inventory.Adjust']).el.querySelector('.page-head')?.textContent).toContain('New adjustment');
  });
});
