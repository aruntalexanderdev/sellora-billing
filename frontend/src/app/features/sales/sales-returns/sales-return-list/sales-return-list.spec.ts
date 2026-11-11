import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs, type ScopeStub } from '../../../../shared/scope/scope-testing';
import { SalesService } from '../../sales.service';
import { SalesReturnList } from './sales-return-list';

const item = {
  id: 'r1', returnNo: 'SR-1', returnDate: '2026-11-06', salesInvoiceId: 's1', invoiceNo: 'INV-1', customerName: 'Walk-in', total: 10, refundedAmount: 0,
  branchId: 'b2', branchCode: 'NTH', branchName: 'North', stockLocationId: 'l1', stockLocationCode: 'VAN1', stockLocationName: 'Van 1',
};

function setup(permissions: string[], scope: ScopeStub = {}) {
  const returns = vi.fn(() => of({ items: [item], total: 1, page: 1, pageSize: 20 }));
  TestBed.configureTestingModule({
    providers: [provideRouter([]), ...provideScopeStubs(scope), { provide: SalesService, useValue: { returns } }, { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } }],
  });
  const fixture = TestBed.createComponent(SalesReturnList);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  return { c: fixture.componentInstance, returns, headers: () => Array.from(el.querySelectorAll('thead th')).map((h) => (h.textContent ?? '').trim()), el };
}

describe('SalesReturnList scope', () => {
  it('sends branchId and stockLocationId (restock location)', () => {
    const { c, returns } = setup([]);
    c.onBranchChange('b2');
    c.onLocationChange('l1');
    expect(returns).toHaveBeenLastCalledWith(expect.objectContaining({ branchId: 'b2', stockLocationId: 'l1', page: 1 }));
  });

  it('shows Branch for multi-branch users and Location with StockLocations.View', () => {
    const a = setup([], { branches: 2 });
    expect(a.headers()).toEqual(expect.arrayContaining(['Branch']));
    expect(a.headers()).not.toContain('Location');
    TestBed.resetTestingModule();
    const b = setup(['StockLocations.View']);
    expect(b.headers()).toContain('Location');
    expect(b.headers()).not.toContain('Branch');
    expect(b.el.querySelector('tbody tr')?.textContent).toContain('Van 1 (VAN1)');
  });
});
