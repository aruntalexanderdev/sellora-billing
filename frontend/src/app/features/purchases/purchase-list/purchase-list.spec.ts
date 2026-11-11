import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { provideScopeStubs, type ScopeStub } from '../../../shared/scope/scope-testing';
import { PurchasesService } from '../purchases.service';
import type { PurchaseListItem } from '../purchases.models';
import { PurchaseList } from './purchase-list';

const item: PurchaseListItem = {
  id: 'p1', purchaseNo: 'PUR-1', supplierName: 'Acme', purchaseDate: '2026-11-06', grandTotal: 500, paymentStatus: 'Due', status: 'Completed',
  currencyCode: 'INR', originalGrandTotal: 500, branchId: 'b2', branchCode: 'NTH', branchName: 'North', stockLocationId: 'l1', stockLocationCode: 'WH', stockLocationName: 'Warehouse',
};

function setup(permissions: string[] = ['Purchase.View'], scope: ScopeStub = {}) {
  const list = vi.fn(() => of({ items: [item], total: 1, page: 1, pageSize: 20 }));
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      ...provideScopeStubs(scope),
      { provide: PurchasesService, useValue: { list } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
    ],
  });
  const fixture = TestBed.createComponent(PurchaseList);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const headers = () => Array.from(el.querySelectorAll('thead th')).map((h) => (h.textContent ?? '').trim());
  return { c: fixture.componentInstance, list, headers, el };
}

describe('PurchaseList scope', () => {
  it('sends branchId and stockLocationId', () => {
    const { c, list } = setup();
    c.onPageChange(3);
    c.onBranchChange('b2');
    c.onLocationChange('l1');
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ branchId: 'b2', stockLocationId: 'l1', page: 1 }));
  });

  it('keeps the row actions column last, after the scope columns', () => {
    const { headers, el } = setup(['Purchase.View', 'StockLocations.View'], { accessScope: 'CompanyWide' });

    expect(headers().slice(-3)).toEqual(['Branch', 'Location', '']);
    const cells = Array.from(el.querySelectorAll('tbody tr td')).map((x) => (x.textContent ?? '').trim());
    expect(cells).toContain('North (NTH)');
    expect(cells).toContain('Warehouse (WH)');
  });

  it('shows no scope columns for a single-branch user', () => {
    const { headers } = setup();
    expect(headers()).not.toContain('Branch');
    expect(headers()).not.toContain('Location');
  });
});
