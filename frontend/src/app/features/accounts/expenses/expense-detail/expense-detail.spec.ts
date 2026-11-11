import { HttpContext, HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { HttpTestingController } from '@angular/common/http/testing';
import { Observable, of, throwError } from 'rxjs';
import { ExpenseDetail } from './expense-detail';
import { ExpensesService } from '../expenses.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../../shared/scope/scope-testing';
import type { ExpenseDetail as Detail } from '../expenses.models';

function detail(o: Partial<Detail> = {}): Detail {
  return {
    id: 'e1', expenseNo: 'EXP-0001', expenseDate: '2026-11-01', categoryId: 'c1', categoryName: 'Rent', currencyId: 'inr', currencyCode: 'INR',
    currencySymbol: '₹', currencyDecimalPrecision: 2, amount: 100, exchangeRateToBase: 1, baseAmount: 100, baseCurrencyCode: 'INR',
    baseCurrencySymbol: '₹', baseCurrencyDecimalPrecision: 2, paymentMethod: 'Cash', reference: null, notes: null, status: 'Active',
    createdByName: 'Ravi', createdAt: '2026-11-01T10:00:00Z', updatedAt: null, cancelledAt: null, cancelledByName: null, cancellationReason: null,
    branchId: 'b1', branchCode: 'B1', branchName: 'Branch 1', ...o,
  } as Detail;
}

function setup(response: Observable<Detail>) {
  const get = vi.fn(() => response);
  TestBed.configureTestingModule({
    imports: [ExpenseDetail],
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'e1' }) } } },
      { provide: ExpensesService, useValue: { get, categories: () => of([]) } },
      { provide: AuthService, useValue: { hasPermission: () => false } },
    ],
  });
  const fixture = TestBed.createComponent(ExpenseDetail);
  fixture.detectChanges();
  return { el: fixture.nativeElement as HTMLElement, get };
}

const text = (el: HTMLElement, sel: string) => (el.querySelector(sel)?.textContent ?? '').replace(/\s+/g, ' ').trim();

describe('ExpenseDetail', () => {
  it('shows the branch in the document scope strip', () => {
    const { el, get } = setup(of(detail()));
    expect(get).toHaveBeenCalledWith('e1');
    expect(text(el, '[data-testid="scope-branch"]')).toBe('Branch 1 (B1)');
  });

  it('shows "Company" for a company-level expense', () => {
    const { el } = setup(of(detail({ branchId: null, branchCode: null, branchName: null })));
    expect(text(el, '[data-testid="scope-branch"]')).toBe('Company');
  });

  it('shows the mapped problem text on a failed load and plain "not found" on 404', () => {
    const fail = (status: number, error: unknown) => throwError(() => new HttpErrorResponse({ status, error }));
    const a = setup(fail(403, { title: 'Forbidden', detail: 'No access to this branch.', status: 403 }));
    expect(text(a.el, '.not-found')).toContain('Forbidden');
    expect(a.el.querySelector('.not-found')?.getAttribute('role')).toBe('alert');
    TestBed.resetTestingModule();
    const b = setup(fail(404, { title: 'Not found', status: 404 }));
    expect(text(b.el, '.not-found')).toBe('Expense not found.');
  });
});

describe('ExpensesService.get', () => {
  it('marks the detail load as the primary load', () => {
    TestBed.configureTestingModule({ providers: [...provideScopeStubs()] });
    const svc = TestBed.inject(ExpensesService);
    const http = TestBed.inject(HttpTestingController);
    svc.get('e1').subscribe();
    const req = http.expectOne((r) => r.url.endsWith('/e1'));
    expect(req.request.context).toBeInstanceOf(HttpContext);
    expect([...req.request.context.keys()].length).toBeGreaterThan(0);
    req.flush(detail());
  });
});
