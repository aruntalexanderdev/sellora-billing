import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CurrentStockReport } from './current-stock-report';
import { AuthService } from '../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../shared/scope/scope-testing';
import { ReportsService } from '../reports.service';
import type { CurrentStockReportRow } from '../reports.models';

const row = (o: Partial<CurrentStockReportRow>): CurrentStockReportRow => ({
  productId: 'p', sku: 'PEN', name: 'Pen', category: null, baseUnitLabel: 'pc', baseDecimalPlaces: 0, quantityOnHand: 122,
  averageCost: 1, stockValue: 122, sellingPricePerBaseUnit: 2, sellingValue: 244, status: 'Normal', ...o,
});

const penUnits = [
  { productUnitId: 'u-pc', unitCode: 'PC', label: 'Piece', factor: 1, isBaseUnit: true },
  { productUnitId: 'u-box', unitCode: 'BOX', label: 'Box', factor: 10, isBaseUnit: false },
  { productUnitId: 'u-fc', unitCode: 'FC', label: 'Full Carton', factor: 100, isBaseUnit: false },
];

function mount(items: CurrentStockReportRow[]) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: ReportsService, useValue: { currentStock: vi.fn(() => of({ items, total: items.length, page: 1, pageSize: 20 })), currentStockCategories: () => of([]) } },
      { provide: AuthService, useValue: { hasPermission: () => false } },
    ],
  });
  const f = TestBed.createComponent(CurrentStockReport);
  f.detectChanges();
  return { c: f.componentInstance, el: f.nativeElement as HTMLElement };
}

describe('CurrentStockReport quantity precision', () => {
  const qtyCell = (c: CurrentStockReport, i: number) => c.rows()[i].cells[2] as { text: string; sub?: string };

  it('rows_pen122_showBreakdownAsSubText', () => {
    const { c, el } = mount([row({ breakdownUnits: penUnits })]);
    expect(qtyCell(c, 0)).toMatchObject({ text: '122', sub: '1 Full Carton, 2 Box, 2 Piece' });
    expect(el.textContent).toContain('1 Full Carton, 2 Box, 2 Piece');
  });

  it('rows_kgProduct_usesBaseDecimalPlaces', () => {
    const { c } = mount([row({ name: 'Rice', baseUnitLabel: 'kg', baseDecimalPlaces: 3, quantityOnHand: 12 })]);
    expect(qtyCell(c, 0).text).toBe('12.000');
  });

  it('rows_baseOnlyOrMissingBreakdown_haveNoSubText', () => {
    const { c } = mount([row({}), row({ breakdownUnits: [penUnits[0]] })]);
    expect(qtyCell(c, 0).sub).toBeUndefined();
    expect(qtyCell(c, 1).sub).toBeUndefined();
  });

  it('rows_zeroStock_haveNoSubText', () => {
    const { c } = mount([row({ quantityOnHand: 0, breakdownUnits: penUnits })]);
    expect(qtyCell(c, 0)).toMatchObject({ text: '0' });
    expect(qtyCell(c, 0).sub).toBeUndefined();
  });
});
