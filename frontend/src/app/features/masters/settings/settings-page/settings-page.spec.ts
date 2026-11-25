import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { CurrenciesService } from '../../../administration/currencies/currencies.service';
import type { BusinessSettings } from '../settings.models';
import { GeoService } from '../../../../shared/geo/geo.service';
import { SettingsService } from '../settings.service';
import { SettingsPage } from './settings-page';

const settings = (over: Partial<BusinessSettings> = {}): BusinessSettings => ({
  id: 's', businessName: 'Acme', addressLine: null, city: null, state: null, gstin: null, phone: null, email: null, invoicePrefix: 'INV',
  baseCurrencyId: 'cur', baseCurrencyCode: 'INR', baseCurrencySymbol: '₹', companyId: 'co', receiptPostingMode: 'Direct', receiptSeparationOfDuties: false, sellingPricesIncludeTax: true, maxBillDiscountPercent: null, countryId: null, countryName: null, stateId: null, stateName: null, ...over,
});

function render(s: BusinessSettings, update = vi.fn(() => of(s)), canEdit = true) {
  TestBed.configureTestingModule({
    imports: [SettingsPage],
    providers: [
      { provide: SettingsService, useValue: { get: () => of(s), update } },
      { provide: CurrenciesService, useValue: { list: () => of([{ id: 'cur', code: 'INR', name: 'Rupee', isActive: true }]) } },
      { provide: AuthService, useValue: { hasPermission: () => canEdit } },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: GeoService, useValue: { lookupCountries: () => of([{ id: 'in', code: 'IN', name: 'India' }]), lookupStates: () => of([{ id: 'kl', countryId: 'in', code: 'KL', name: 'Kerala', gstStateCode: '32' }]) } },
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

  it('PUT posts countryId/stateId and no legacy state (D6, D22)', () => {
    const { fixture, update } = render(settings({ countryId: 'in', countryName: 'India', stateId: 'kl', stateName: 'Kerala' }));
    fixture.componentInstance.submit();
    const body = (update.mock.calls[0] as unknown[])[0] as Record<string, unknown>;
    expect(body).toMatchObject({ countryId: 'in', stateId: 'kl' });
    expect('state' in body).toBe(false);
  });

  it('clearing the country posts null links', () => {
    const { fixture, update } = render(settings({ countryId: 'in', countryName: 'India', stateId: 'kl', stateName: 'Kerala' }));
    fixture.componentInstance.form.patchValue({ countryId: '', stateId: 'kl' });
    fixture.componentInstance.submit();
    expect((update.mock.calls[0] as unknown[])[0]).toMatchObject({ countryId: null, stateId: null });
  });

  it('shows the legacy state text as a read-only hint while no state is linked', () => {
    const { el } = render(settings({ state: 'Kerla' }));
    expect(el.querySelector('[data-testid="geo-legacy"]')?.textContent).toContain('Kerla');
    expect(el.querySelector('input[formControlName="state"]')).toBeNull();
  });
});

describe('SettingsPage tax and discount fields (TP-1)', () => {
  const body = (update: ReturnType<typeof vi.fn>) => (update.mock.calls[0] as unknown[])[0] as Record<string, unknown>;

  it('loads the price basis and the discount cap', () => {
    const { el } = render(settings({ sellingPricesIncludeTax: false, maxBillDiscountPercent: 15 }));
    expect((el.querySelector('[data-testid="prices-include-tax"]') as HTMLInputElement).checked).toBe(false);
    expect((el.querySelector('[data-testid="max-bill-discount"]') as HTMLInputElement).value).toBe('15');
  });

  it('always posts both fields; an empty cap posts null (no cap)', () => {
    const { fixture, update } = render(settings());
    fixture.componentInstance.submit();
    expect(body(update)).toMatchObject({ sellingPricesIncludeTax: true, maxBillDiscountPercent: null });
    expect('maxBillDiscountPercent' in body(update)).toBe(true);
  });

  it('posts the chosen basis and cap', () => {
    const { fixture, update } = render(settings());
    fixture.componentInstance.form.patchValue({ sellingPricesIncludeTax: false, maxBillDiscountPercent: 12.5 });
    fixture.componentInstance.submit();
    expect(body(update)).toMatchObject({ sellingPricesIncludeTax: false, maxBillDiscountPercent: 12.5 });
  });

  it('rejects a cap outside 0-100 without calling the API', () => {
    const { fixture, update } = render(settings());
    fixture.componentInstance.form.patchValue({ maxBillDiscountPercent: 101 });
    fixture.componentInstance.submit();
    expect(update).not.toHaveBeenCalled();
    fixture.componentInstance.form.patchValue({ maxBillDiscountPercent: -1 });
    fixture.componentInstance.submit();
    expect(update).not.toHaveBeenCalled();
  });

  it('maps a server error on maxBillDiscountPercent onto the field', () => {
    const err = new HttpErrorResponse({ status: 400, error: { errors: { MaxBillDiscountPercent: ['Must be between 0 and 100.'] } } });
    const { fixture, el } = render(settings(), vi.fn(() => throwError(() => err)));
    fixture.componentInstance.submit();
    fixture.detectChanges();
    expect(el.textContent).toContain('Must be between 0 and 100.');
  });
});
