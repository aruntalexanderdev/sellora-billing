import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { FundForm, sourceFields } from './fund-form';
import { CashBankService } from '../cash-bank.service';
import { CustomersService } from '../../../sales/customers/customers.service';

function setup(fund = vi.fn(() => of({ transactionId: 't1' }))) {
  TestBed.configureTestingModule({
    imports: [FundForm],
    providers: [
      provideRouter([{ path: '**', children: [] }]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'a1' }) } } },
      { provide: CashBankService, useValue: { get: () => of({ id: 'a1', name: 'Main Cash' }), fund } },
      { provide: CustomersService, useValue: { list: () => of({ items: [{ id: 'c1', name: 'Asha', phone: null, address: null, isWalkIn: false, isActive: true }] }) } },
    ],
  });
  const fixture = TestBed.createComponent(FundForm);
  fixture.detectChanges();
  return { fixture, cmp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, fund };
}

describe('FundForm', () => {
  it('maps sources to their dynamic fields', () => {
    expect(sourceFields('CustomerReceipt')).toMatchObject({ customer: true, reference: { required: true } });
    expect(sourceFields('Other').reason).toBe(true);
    expect(sourceFields('Loan').counterparty?.label).toBe('Lender');
    expect(sourceFields('CashDeposit').counterparty).toBeNull();
  });

  it('requires customer and reference for a customer receipt, and a reason for Other', () => {
    const { cmp, fixture, el } = setup();
    cmp.form.patchValue({ source: 'CustomerReceipt', amount: 10 });
    fixture.detectChanges();
    expect(cmp.form.invalid).toBe(true);
    expect(el.querySelector('#fund-customer')).not.toBeNull();
    cmp.form.patchValue({ customerId: 'c1', sourceReference: 'R-1' });
    expect(cmp.form.valid).toBe(true);
    cmp.form.patchValue({ source: 'Other' });
    expect(cmp.form.controls.reason.valid).toBe(false);
  });

  it('posts the funding with an idempotency key and omits hidden fields', () => {
    const { cmp, fund } = setup();
    cmp.form.patchValue({ source: 'Loan', amount: 500, counterparty: 'Bank X', sourceReference: 'L-9' });
    cmp.submit();
    const [id, body, key] = fund.mock.calls[0] as unknown as [string, Record<string, unknown>, string];
    expect(id).toBe('a1');
    expect(body).toMatchObject({ source: 'Loan', amount: 500, counterparty: 'Bank X', sourceReference: 'L-9', customerId: null, reason: null });
    expect(key).toMatch(/.+/);
  });

  it('maps server 400 field errors onto the controls', () => {
    const fund = vi.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { amount: ['Too big'], fundingDate: ['Future'] } } })));
    const { cmp } = setup(fund);
    cmp.form.patchValue({ amount: 5 });
    cmp.submit();
    expect(cmp.form.controls.amount.errors?.['server']).toBe('Too big');
    expect(cmp.form.controls.fundingDate.errors?.['server']).toBe('Future');
  });
});
