import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { GeoService } from '../../../../shared/geo/geo.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { CurrenciesService } from '../../../administration/currencies/currencies.service';
import type { BusinessSettings } from '../settings.models';
import { SettingsService } from '../settings.service';
import { SettingsPage } from './settings-page';

const settings = (over: Partial<BusinessSettings> = {}): BusinessSettings => ({
  id: 's', businessName: 'Acme', addressLine: null, city: null, state: null, gstin: null, phone: null, email: null, invoicePrefix: 'INV',
  baseCurrencyId: 'cur', baseCurrencyCode: 'INR', baseCurrencySymbol: '₹', companyId: 'co', receiptPostingMode: 'Direct', receiptSeparationOfDuties: false, transferApprovalSeparationOfDuties: true,
  sellingPricesIncludeTax: true, maxBillDiscountPercent: null, countryId: null, countryName: null, stateId: null, stateName: null, ...over,
});

function render(s: BusinessSettings) {
  const update = vi.fn(() => of(s));
  TestBed.configureTestingModule({
    imports: [SettingsPage],
    providers: [
      { provide: SettingsService, useValue: { get: () => of(s), update } },
      { provide: CurrenciesService, useValue: { list: () => of([{ id: 'cur', code: 'INR', name: 'Rupee', isActive: true }]) } },
      { provide: AuthService, useValue: { hasPermission: () => true } },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: GeoService, useValue: { lookupCountries: () => of([]), lookupStates: () => of([]) } },
    ],
  });
  const fixture = TestBed.createComponent(SettingsPage);
  fixture.detectChanges();
  return { fixture, c: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, update };
}
const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`);

describe('SettingsPage HSN required from (T11/T34)', () => {
  it('loads the date and shows the missing-HSN count with a warning while a date is set', () => {
    const { el } = render(settings({ hsnRequiredFrom: '2026-12-01', productsWithoutHsnCount: 4 }));
    expect((q(el, 'hsn-required-from') as HTMLInputElement).value).toBe('2026-12-01');
    expect(q(el, 'hsn-missing-count')?.textContent).toContain('4');
    expect(q(el, 'hsn-warning')).not.toBeNull();
  });

  it('shows the count but no warning when there is no date, or nothing is missing', () => {
    expect(q(render(settings({ hsnRequiredFrom: null, productsWithoutHsnCount: 4 })).el, 'hsn-warning')).toBeNull();
    TestBed.resetTestingModule();
    expect(q(render(settings({ hsnRequiredFrom: '2026-12-01', productsWithoutHsnCount: 0 })).el, 'hsn-warning')).toBeNull();
  });

  it('warns while a date is being set, and hides the count when unknown', () => {
    const { fixture, c, el } = render(settings({ hsnRequiredFrom: null, productsWithoutHsnCount: 2 }));
    c.form.controls.hsnRequiredFrom.setValue('2026-12-15');
    (q(el, 'hsn-required-from') as HTMLInputElement).dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(q(el, 'hsn-warning')).not.toBeNull();
    TestBed.resetTestingModule();
    expect(q(render(settings({ productsWithoutHsnCount: null })).el, 'hsn-missing-count')).toBeNull();
  });

  it('omits the HSN fields when untouched (unchanged on the server)', () => {
    const { c } = render(settings());
    const req = c.buildRequest();
    expect('hsnRequiredFrom' in req).toBe(false);
    expect('clearHsnRequiredFrom' in req).toBe(false);
  });

  it('sends the chosen date', () => {
    const { c } = render(settings());
    c.form.controls.hsnRequiredFrom.setValue('2027-01-01');
    expect(c.buildRequest().hsnRequiredFrom).toBe('2027-01-01');
  });

  it('Clear sends clearHsnRequiredFrom: true and no date, and hides the warning', () => {
    const { fixture, c, el, update } = render(settings({ hsnRequiredFrom: '2026-12-01', productsWithoutHsnCount: 4 }));
    (q(el, 'hsn-clear') as HTMLElement).click();
    fixture.detectChanges();
    expect(c.form.controls.hsnRequiredFrom.value).toBe('');
    expect(q(el, 'hsn-warning')).toBeNull();
    const req = c.buildRequest();
    expect(req.clearHsnRequiredFrom).toBe(true);
    expect('hsnRequiredFrom' in req).toBe(false);
    c.submit();
    expect(update).toHaveBeenCalledWith(expect.objectContaining({ clearHsnRequiredFrom: true }));
  });
});
