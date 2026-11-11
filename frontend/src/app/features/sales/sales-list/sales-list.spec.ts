import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { provideScopeStubs, type ScopeStub } from '../../../shared/scope/scope-testing';
import { SalesService } from '../sales.service';
import type { SalesInvoiceListItem } from '../sales.models';
import { SalesList } from './sales-list';

const item: SalesInvoiceListItem = {
  id: 's1', invoiceNo: 'INV-1', customerName: 'Walk-in', invoiceDate: '2026-11-06', grandTotal: 100, paidAmount: 100, dueAmount: 0,
  paymentStatus: 'Paid', status: 'Completed', branchId: 'b2', branchCode: 'NTH', branchName: 'North', stockLocationId: 'l1', stockLocationCode: 'VAN1', stockLocationName: 'Van 1',
};

function setup(permissions: string[] = ['Sales.View'], scope: ScopeStub = {}, response = of({ items: [item], total: 1, page: 1, pageSize: 20 })) {
  const list = vi.fn(() => response);
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      ...provideScopeStubs(scope),
      { provide: SalesService, useValue: { list } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
    ],
  });
  const fixture = TestBed.createComponent(SalesList);
  fixture.detectChanges();
  const headers = () => Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('thead th')).map((h) => (h.textContent ?? '').trim());
  return { fixture, c: fixture.componentInstance, list, headers, el: fixture.nativeElement as HTMLElement };
}

describe('SalesList scope', () => {
  it('turns the branch and location filters into query params and resets to page 1', () => {
    const { c, list } = setup();
    c.onPageChange(2);

    c.onBranchChange('b2');
    c.onLocationChange('l1');

    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ branchId: 'b2', stockLocationId: 'l1', page: 1 }));
  });

  it('hides Branch and Location columns for a single-branch user without location access', () => {
    const { headers, el } = setup();

    expect(headers()).not.toContain('Branch');
    expect(headers()).not.toContain('Location');
    expect(el.querySelector('.report-filters')).toBeNull();
  });

  it.each([
    ['company-wide', { accessScope: 'CompanyWide' }],
    ['multi-branch', { branches: 3 }],
    ['all-branches', { allBranches: true }],
  ] as [string, ScopeStub][])('shows the Branch column for a %s user', (_l, scope) => {
    const { headers, el } = setup(['Sales.View'], scope);

    expect(headers()).toContain('Branch');
    expect(el.querySelector('tbody tr')?.textContent).toContain('North (NTH)');
  });

  it('shows the Location column and filter bar with StockLocations.View', () => {
    const { headers, el } = setup(['Sales.View', 'StockLocations.View']);

    expect(headers()).toContain('Location');
    expect(el.querySelector('tbody tr')?.textContent).toContain('Van 1 (VAN1)');
    expect(el.querySelector('.report-filters')).not.toBeNull();
  });

  it('shows the mapped server error', () => {
    const { el } = setup(['Sales.View'], {}, throwError(() => new HttpErrorResponse({ status: 403, error: { status: 403, code: 'branch_access_denied' } })) as never);

    expect(el.querySelector('[data-testid="load-error"]')?.textContent).toContain('You do not have access to that branch');
  });
});
