import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs, type ScopeStub } from '../../../../shared/scope/scope-testing';
import { PurchasesService } from '../../purchases.service';
import { PurchaseReturnList } from './purchase-return-list';

const item = {
  id: 'r1', returnNo: 'PR-1', returnDate: '2026-11-06', purchaseOrderId: 'p1', purchaseNo: 'PUR-1', supplierName: 'Acme', total: 10, originalTotal: 10, currencyCode: 'INR', refundedAmount: 0,
  branchId: 'b2', branchCode: 'NTH', branchName: 'North', stockLocationId: 'l1', stockLocationCode: 'WH', stockLocationName: 'Warehouse',
};

function setup(permissions: string[], scope: ScopeStub = {}) {
  const returns = vi.fn(() => of({ items: [item], total: 1, page: 1, pageSize: 20 }));
  TestBed.configureTestingModule({
    providers: [provideRouter([]), ...provideScopeStubs(scope), { provide: PurchasesService, useValue: { returns } }, { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } }],
  });
  const fixture = TestBed.createComponent(PurchaseReturnList);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  return { c: fixture.componentInstance, returns, headers: () => Array.from(el.querySelectorAll('thead th')).map((h) => (h.textContent ?? '').trim()), el };
}

describe('PurchaseReturnList scope', () => {
  it('sends branchId and stockLocationId (issuing location)', () => {
    const { c, returns } = setup([]);
    c.onBranchChange('b2');
    c.onLocationChange('l1');
    expect(returns).toHaveBeenLastCalledWith(expect.objectContaining({ branchId: 'b2', stockLocationId: 'l1', page: 1 }));
  });

  it('shows Branch and Location columns by the visibility rules', () => {
    const a = setup(['StockLocations.View'], { accessScope: 'CompanyWide' });
    expect(a.headers().slice(-2)).toEqual(['Branch', 'Location']);
    expect(a.el.querySelector('tbody tr')?.textContent).toContain('Warehouse (WH)');
    TestBed.resetTestingModule();
    const b = setup([]);
    expect(b.headers()).toEqual(['Return', 'Date', 'Purchase', 'Supplier', 'Credit']);
  });
});
