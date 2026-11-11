import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs, type ScopeStub } from '../../../../shared/scope/scope-testing';
import { ExpensesService } from '../expenses.service';
import { ExpenseList } from './expense-list';

const mk = (id: string, branchId: string | null) => ({
  id, expenseNo: `EXP-${id}`, expenseDate: '2026-11-06', categoryName: 'Rent', reference: null, paymentMethod: 'Cash', amount: 10, currencyCode: 'INR', baseAmount: 10,
  status: 'Active', branchId, branchCode: branchId ? 'NTH' : null, branchName: branchId ? 'North' : null,
});

function setup(scope: ScopeStub = {}) {
  const list = vi.fn(() => of({ items: [mk('1', 'b2'), mk('2', null)], total: 2, page: 1, pageSize: 20 }));
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      ...provideScopeStubs(scope),
      { provide: ExpensesService, useValue: { list, categories: () => of([]) } },
      { provide: AuthService, useValue: { hasPermission: () => false } },
    ],
  });
  const fixture = TestBed.createComponent(ExpenseList);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  return { c: fixture.componentInstance, list, el, headers: () => Array.from(el.querySelectorAll('thead th')).map((h) => (h.textContent ?? '').trim()) };
}

describe('ExpenseList branch scope', () => {
  it('sends branchId', () => {
    const { c, list } = setup();
    c.onBranchChange('b2');
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ branchId: 'b2', page: 1 }));
  });

  it('shows a Branch column (company-level expenses read "Company") only when the user spans branches', () => {
    const a = setup({ accessScope: 'CompanyWide' });
    expect(a.headers()).toContain('Branch');
    const rows = Array.from(a.el.querySelectorAll('tbody tr')).map((r) => r.textContent ?? '');
    expect(rows[0]).toContain('North (NTH)');
    expect(rows[1]).toContain('Company');
    TestBed.resetTestingModule();
    expect(setup().headers()).not.toContain('Branch');
  });
});
