import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { CurrenciesService } from '../../../administration/currencies/currencies.service';
import type { BusinessSettings } from '../settings.models';
import { SettingsService } from '../settings.service';
import { SettingsPage } from './settings-page';

const settings = (over: Partial<BusinessSettings> = {}): BusinessSettings => ({
  id: 's', businessName: 'Acme', addressLine: null, city: null, state: null, gstin: null, phone: null, email: null, invoicePrefix: 'INV',
  baseCurrencyId: 'cur', baseCurrencyCode: 'INR', baseCurrencySymbol: '₹', companyId: 'co', receiptPostingMode: 'Direct', receiptSeparationOfDuties: false, ...over,
});

function render(s: BusinessSettings, update = vi.fn(() => of(s)), canEdit = true) {
  TestBed.configureTestingModule({
    imports: [SettingsPage],
    providers: [
      { provide: SettingsService, useValue: { get: () => of(s), update } },
      { provide: CurrenciesService, useValue: { list: () => of([{ id: 'cur', code: 'INR', name: 'Rupee', isActive: true }]) } },
      { provide: AuthService, useValue: { hasPermission: () => canEdit } },
      { provide: ToastService, useValue: { show: vi.fn() } },
    ],
  });
  const fixture = TestBed.createComponent(SettingsPage);
  fixture.detectChanges();
  return { fixture, el: fixture.nativeElement as HTMLElement, update };
}

describe('SettingsPage receipt approval (FI-D39)', () => {
  it('loads the policy into the form and explains that a change only affects later receipts', () => {
    const { el } = render(settings({ receiptPostingMode: 'SupervisorApproval', receiptSeparationOfDuties: true }));
    expect((el.querySelector('[data-testid="receipt-posting-mode"]') as HTMLSelectElement).value).toBe('SupervisorApproval');
    expect((el.querySelector('[data-testid="receipt-separation"]') as HTMLInputElement).checked).toBe(true);
    expect(el.querySelector('[data-testid="receipt-policy-help"]')?.textContent).toMatch(/only to receipts recorded afterwards/i);
  });

  it('defaults to Direct without separation when the API omits the fields', () => {
    const { fixture } = render({ ...settings(), receiptPostingMode: undefined, receiptSeparationOfDuties: undefined } as unknown as BusinessSettings);
    expect(fixture.componentInstance.form.getRawValue()).toMatchObject({ receiptPostingMode: 'Direct', receiptSeparationOfDuties: false });
  });

  it('saves the chosen policy in the PUT body', () => {
    const { fixture, update } = render(settings());
    fixture.componentInstance.form.patchValue({ receiptPostingMode: 'SupervisorApproval', receiptSeparationOfDuties: true });
    fixture.componentInstance.submit();
    expect(update).toHaveBeenCalledWith(expect.objectContaining({ receiptPostingMode: 'SupervisorApproval', receiptSeparationOfDuties: true, businessName: 'Acme' }));
  });

  it('maps a server error on receiptPostingMode onto the control', () => {
    const err = new HttpErrorResponse({ status: 400, error: { title: 'x', errors: { ReceiptPostingMode: ['Choose Direct or SupervisorApproval.'] } } });
    const { fixture, el } = render(settings(), vi.fn(() => throwError(() => err)));
    fixture.componentInstance.submit();
    fixture.detectChanges();
    expect(fixture.componentInstance.form.controls.receiptPostingMode.errors?.['server']).toBe('Choose Direct or SupervisorApproval.');
    expect(el.textContent).toContain('Choose Direct or SupervisorApproval.');
  });

  it('is read-only without Masters.Edit', () => {
    const { fixture } = render(settings(), undefined, false);
    expect(fixture.componentInstance.form.disabled).toBe(true);
  });
});
