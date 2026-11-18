import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { TransferForm } from './transfer-form';
import { CashBankService } from '../cash-bank.service';
import { availableBalance } from '../cash-bank.models';

const acct = (id: string, balance: number | null, overdraftLimit = 0) => ({ id, name: id, kind: 'Bank', reference: null, isActive: true, balance, reconciledBalance: balance, unreconciledCount: 0, overdraftLimit });

function setup(transfer = vi.fn(() => of(undefined))) {
  TestBed.configureTestingModule({
    imports: [TransferForm],
    providers: [
      provideRouter([{ path: '**', children: [] }]),
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
      { provide: CashBankService, useValue: { list: () => of({ items: [acct('a', 100, 50), acct('b', 0), acct('hidden', null)], baseCurrencySymbol: '₹' }), transfer } },
    ],
  });
  const fixture = TestBed.createComponent(TransferForm);
  fixture.detectChanges();
  return { fixture, cmp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, transfer };
}

describe('TransferForm', () => {
  it('computes available balance as balance + overdraft, nothing for hidden balances', () => {
    expect(availableBalance({ balance: 100, overdraftLimit: 50 })).toBe(150);
    expect(availableBalance({ balance: null, overdraftLimit: 50 })).toBeNull();
  });

  it('shows available balance and warns only beyond it', () => {
    const { cmp, fixture, el } = setup();
    cmp.form.patchValue({ fromAccountId: 'a', amount: 120 });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="available"]')?.textContent).toContain('150.00');
    expect(el.querySelector('[data-testid="overdraw"]')).toBeNull();
    cmp.form.patchValue({ amount: 151 });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="overdraw"]')).not.toBeNull();
    cmp.form.patchValue({ fromAccountId: 'hidden' });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="available"]')).toBeNull();
  });

  it('sends an Idempotency-Key and reuses it for an identical retry after a network failure', () => {
    const transfer = vi.fn().mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 0 }))).mockReturnValue(of(undefined));
    const { cmp } = setup(transfer);
    cmp.form.patchValue({ fromAccountId: 'a', toAccountId: 'b', amount: 10 });
    cmp.submit();
    cmp.submit();
    expect(transfer.mock.calls[0][1]).toBeTruthy();
    expect(transfer.mock.calls[1][1]).toBe(transfer.mock.calls[0][1]);
  });

  it('maps overdraft and source-account 400s onto amount and fromAccountId', () => {
    const transfer = vi.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { amount: ['Overdraft limit exceeded'], fromAccountId: ['Not an account of the active branch'] } } })));
    const { cmp, fixture, el } = setup(transfer);
    cmp.form.patchValue({ fromAccountId: 'a', toAccountId: 'b', amount: 10 });
    cmp.submit();
    fixture.detectChanges();
    expect(cmp.form.controls.amount.errors?.['server']).toBe('Overdraft limit exceeded');
    expect(el.textContent).toContain('Not an account of the active branch');
    expect(el.textContent).not.toContain('Enter an amount greater than zero');
  });
});
