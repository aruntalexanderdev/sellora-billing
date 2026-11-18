import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AccountForm } from './account-form';
import { CashBankService } from '../cash-bank.service';

function setup() {
  const create = vi.fn(() => of({}));
  TestBed.configureTestingModule({
    imports: [AccountForm],
    providers: [
      provideRouter([{ path: '**', children: [] }]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({}) } } },
      { provide: CashBankService, useValue: { create } },
    ],
  });
  const fixture = TestBed.createComponent(AccountForm);
  fixture.detectChanges();
  return { fixture, cmp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, create };
}

describe('AccountForm overdraft limit', () => {
  it('is hidden for cash and forced to 0 on submit', () => {
    const { cmp, fixture, el, create } = setup();
    cmp.form.patchValue({ name: 'Drawer', overdraftLimit: 500 });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="overdraft-limit"]')).toBeNull();
    cmp.submit();
    expect((create.mock.calls[0] as unknown[])[0]).toMatchObject({ kind: 'Cash', overdraftLimit: 0 });
  });

  it('is shown and sent for a bank account', () => {
    const { cmp, fixture, el, create } = setup();
    cmp.form.patchValue({ name: 'HDFC', kind: 'Bank', overdraftLimit: 500 });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="overdraft-limit"]')).not.toBeNull();
    cmp.submit();
    expect((create.mock.calls[0] as unknown[])[0]).toMatchObject({ kind: 'Bank', overdraftLimit: 500 });
  });
});
