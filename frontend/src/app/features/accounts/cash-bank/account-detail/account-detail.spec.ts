import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AccountDetail, currentMonthRange } from './account-detail';
import { CashBankService } from '../cash-bank.service';
import { FinancialTransactionsService } from '../../financial-transactions/financial-transactions.service';
import { AuthService } from '../../../../core/auth/auth.service';
import type { FinancialTransaction } from '../../financial-transactions/financial-transactions.models';

const txn = (o: Partial<FinancialTransaction>): FinancialTransaction =>
  ({ id: 't1', transactionDate: '2026-11-01', type: 'Funding', direction: 'In', status: 'Active', party: '', relatedType: null, relatedId: null, relatedNo: null, paymentMethod: 'Cash', amount: 100, currencyCode: 'INR', exchangeRateToBase: 1, baseAmount: 100, createdAt: '', accountId: 'a1', accountName: 'Main', isReconciled: false, notes: null, reversedByTransactionId: null, reversalOfTransactionId: null, fundingSource: 'Loan', reason: null, sourceReference: null, counterparty: null, ...o }) as FinancialTransaction;

const statement = { accountId: 'a1', from: '2026-11-01', to: '2026-11-30', openingBalance: 10, closingBalance: 110, totalIn: 100, totalOut: 0, rows: [{ transactionId: 't1', date: '2026-11-01', type: 'Funding', fundingSource: 'Loan', description: 'Loan', reference: null, moneyIn: 100, moneyOut: 0, runningBalance: 110, reconciled: false, isReversal: false, reversedByTransactionId: 'r1' }] };

function setup(o: { perms?: string[]; rows?: FinancialTransaction[]; statement?: unknown; reverse?: unknown } = {}) {
  const perms = o.perms ?? ['CashBank.Fund', 'CashBank.Reverse'];
  const svc = {
    get: () => of({ id: 'a1', name: 'Main', kind: 'Cash', reference: null, isActive: true, balance: 10, reconciledBalance: 0, unreconciledCount: 0, overdraftLimit: 0 }),
    statement: vi.fn(() => (o.statement ?? of(statement)) as never),
    reverse: vi.fn(() => (o.reverse ?? of({ reversalTransactionIds: ['r1'] })) as never),
  };
  TestBed.configureTestingModule({
    imports: [AccountDetail],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'a1' }) } } },
      { provide: CashBankService, useValue: svc },
      { provide: FinancialTransactionsService, useValue: { list: () => of({ items: o.rows ?? [txn({})], total: 1, baseCurrencySymbol: '₹' }) } },
      { provide: AuthService, useValue: { hasPermission: (p: string) => perms.includes(p) } },
    ],
  });
  const fixture = TestBed.createComponent(AccountDetail);
  fixture.detectChanges();
  return { fixture, cmp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, svc };
}

describe('AccountDetail', () => {
  it('defaults the statement range to the current month', () => {
    expect(currentMonthRange(new Date(2026, 1, 10))).toEqual({ from: '2026-02-01', to: '2026-02-28' });
  });

  it('loads the statement on the tab and shows opening, closing and reversal markers', () => {
    const { cmp, fixture, el, svc } = setup();
    cmp.selectTab('statement');
    fixture.detectChanges();
    expect(svc.statement).toHaveBeenCalledWith('a1', cmp.from(), cmp.to());
    expect(el.querySelector('[data-testid="st-opening"]')?.textContent).toContain('10.00');
    expect(el.querySelector('[data-testid="st-closing"]')?.textContent).toContain('110.00');
    expect(el.querySelector('[data-testid="st-row"]')?.textContent).toContain('Reversed');
  });

  it('shows a friendly message when the statement is forbidden', () => {
    const { cmp, fixture, el } = setup({ statement: throwError(() => new HttpErrorResponse({ status: 403 })) });
    cmp.selectTab('statement');
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="statement-error"]')?.textContent).toContain('company-level');
  });

  it('offers Reverse only for unreconciled, unreversed funding/transfer/opening rows with the permission', () => {
    const { cmp } = setup();
    expect(cmp.canReverseRow(txn({}))).toBe(true);
    expect(cmp.canReverseRow(txn({ type: 'Transfer' }))).toBe(true);
    expect(cmp.canReverseRow(txn({ isReconciled: true }))).toBe(false);
    expect(cmp.canReverseRow(txn({ reversedByTransactionId: 'x' }))).toBe(false);
    expect(cmp.canReverseRow(txn({ type: 'CustomerPayment' }))).toBe(false);
    TestBed.resetTestingModule();
    expect(setup({ perms: [] }).cmp.canReverseRow(txn({}))).toBe(false);
  });

  it('requires a reason, posts the reversal and surfaces server errors', () => {
    const { cmp, svc } = setup();
    cmp.startReverse(txn({}));
    cmp.confirmReverse();
    expect(svc.reverse).not.toHaveBeenCalled();
    expect(cmp.reverseError()).toBe('A reason is required.');
    cmp.reverseReason.set('Entered twice');
    cmp.confirmReverse();
    expect(svc.reverse).toHaveBeenCalledWith('t1', 'Entered twice', expect.any(String));
    expect(cmp.reversing()).toBeNull();
  });

  it('shows the server message when the reversal is rejected', () => {
    const { cmp } = setup({ reverse: throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { id: ['already reversed'] } } })) });
    cmp.startReverse(txn({}));
    cmp.reverseReason.set('x');
    cmp.confirmReverse();
    expect(cmp.reverseError()).toBe('already reversed');
    expect(cmp.reversing()).not.toBeNull();
  });

  it('shows Add funds only with CashBank.Fund', () => {
    expect(setup().el.textContent).toContain('Add funds');
    TestBed.resetTestingModule();
    expect(setup({ perms: [] }).el.textContent).not.toContain('Add funds');
  });
});
