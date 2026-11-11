import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { provideScopeStubs, type ScopeStub } from '../../shared/scope/scope-testing';
import { CurrentStockReport } from './current-stock-report/current-stock-report';
import { ProfitSummaryReport } from './profit-summary-report/profit-summary-report';
import { PurchaseReport } from './purchase-report/purchase-report';
import { ReportsService } from './reports.service';
import { SalesReport } from './sales-report/sales-report';

const page = (items: unknown[]) => of({ items, total: items.length, page: 1, pageSize: 20 });

function mount<T>(type: new () => T, svc: Record<string, unknown>, scope: ScopeStub = {}, permissions: string[] = []) {
  TestBed.configureTestingModule({
    providers: [
      ...provideScopeStubs(scope),
      { provide: ReportsService, useValue: svc },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
    ],
  });
  const fixture = TestBed.createComponent(type);
  fixture.detectChanges();
  return { c: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
}
const headers = (el: HTMLElement) => Array.from(el.querySelectorAll('thead th')).map((h) => (h.textContent ?? '').trim());

describe('report scope filters', () => {
  it('sales report: branch filter -> branchId, Branch column only for multi-branch callers', () => {
    const row = { id: 's1', invoiceNo: 'INV-1', invoiceDate: '2026-11-06', customerName: 'W', subtotal: 1, taxTotal: 0, discountTotal: 0, grandTotal: 1, paidAmount: 1, dueAmount: 0, status: 'Completed', branchId: 'b2', branchCode: 'NTH', branchName: 'North' };
    const sales = vi.fn(() => page([row]));
    const { c, el } = mount(SalesReport, { sales }, { branches: 2 });

    c.onBranchChange('b2');
    expect(sales).toHaveBeenLastCalledWith(expect.objectContaining({ branchId: 'b2', page: 1 }));
    expect(headers(el)).toContain('Branch');
    expect(el.querySelector('tbody tr')?.textContent).toContain('North (NTH)');

    TestBed.resetTestingModule();
    expect(headers(mount(SalesReport, { sales }).el)).not.toContain('Branch');
  });

  it('purchase report: branch filter -> branchId and Branch column', () => {
    const row = { id: 'p1', purchaseNo: 'PUR-1', purchaseDate: '2026-11-06', supplierName: 'A', subtotal: 1, taxTotal: 0, otherChargesTotal: 0, grandTotal: 1, paidAmount: 0, dueAmount: 1, status: 'Completed', currencyCode: 'INR', originalGrandTotal: 1, branchId: 'b2', branchCode: 'NTH', branchName: 'North' };
    const purchases = vi.fn(() => page([row]));
    const { c, el } = mount(PurchaseReport, { purchases }, { accessScope: 'CompanyWide' });

    c.onBranchChange('b2');
    expect(purchases).toHaveBeenLastCalledWith(expect.objectContaining({ branchId: 'b2', page: 1 }));
    expect(headers(el).at(-1)).toBe('Branch');
  });

  it('profit summary: branch filter -> branchId', () => {
    const profitSummary = vi.fn(() => of({ dateFrom: null, dateTo: null, invoiceCount: 0, salesRevenue: 0, costOfGoodsSold: 0, grossProfit: 0, grossMarginPercent: null }));
    const { c } = mount(ProfitSummaryReport, { profitSummary }, { branches: 2 });

    c.onBranchChange('b2');
    expect(profitSummary).toHaveBeenLastCalledWith(expect.objectContaining({ branchId: 'b2' }));
  });

  it('current stock: location filter -> stockLocationId, shown only with StockLocations.View; quantity is not rounded to 2 dp by toFixed', () => {
    const row = { productId: 'p', sku: 'RICE', name: 'Rice', category: null, baseUnitLabel: 'kg', quantityOnHand: 1.25, averageCost: 1, stockValue: 1, sellingPricePerBaseUnit: null, sellingValue: null, status: 'Normal' };
    const currentStock = vi.fn(() => page([row]));
    const base = { currentStock, currentStockCategories: () => of([]) };
    const a = mount(CurrentStockReport, base, {}, ['StockLocations.View']);

    a.c.onLocationChange('loc1');
    expect(currentStock).toHaveBeenLastCalledWith(expect.objectContaining({ stockLocationId: 'loc1', page: 1 }));
    expect(a.el.querySelector('.report-filters')).not.toBeNull();
    expect(a.el.querySelector('tbody tr')?.textContent).toContain('1.25');

    TestBed.resetTestingModule();
    expect(mount(CurrentStockReport, base).el.querySelector('.report-filters')).toBeNull();
  });
});
