import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { GeoService } from '../../../../shared/geo/geo.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { CurrenciesService } from '../../../administration/currencies/currencies.service';
import { SettingsService } from '../../../masters/settings/settings.service';
import type { SupplierItem } from '../suppliers.models';
import { SuppliersService } from '../suppliers.service';
import { SupplierForm } from './supplier-form';

const supplier = (o: Partial<SupplierItem> = {}): SupplierItem => ({
  id: 's1', name: 'Vendor', city: null, gstin: null, paymentTerms: null, isActive: true, currencyId: 'cur', currencyCode: 'INR', currencySymbol: '₹', currencyDecimalPrecision: 2,
  countryId: null, countryName: null, stateId: null, stateName: null, ...o,
});

function mount(id: string | null, loaded = supplier()) {
  const service = { get: vi.fn(() => of(loaded)), create: vi.fn(() => of(supplier())), update: vi.fn(() => of(supplier())) };
  TestBed.configureTestingModule({
    imports: [SupplierForm],
    providers: [
      { provide: SuppliersService, useValue: service },
      { provide: CurrenciesService, useValue: { list: () => of([{ id: 'cur', code: 'INR', name: 'Rupee', isActive: true, isBase: true }]) } },
      { provide: SettingsService, useValue: { get: () => of({ countryId: 'in' }) } },
      { provide: AuthService, useValue: { hasPermission: () => true } },
      { provide: GeoService, useValue: { lookupCountries: () => of([{ id: 'in', code: 'IN', name: 'India' }]), lookupStates: () => of([]) } },
      { provide: Router, useValue: { navigateByUrl: vi.fn() } },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } } },
    ],
  });
  const fixture = TestBed.createComponent(SupplierForm);
  fixture.detectChanges();
  return { component: fixture.componentInstance, service };
}

describe('SupplierForm country/state', () => {
  it('new supplier defaults to the Business Settings country (D7) and posts the links', () => {
    const { component, service } = mount(null);
    expect(component.form.controls.countryId.value).toBe('in');
    component.form.patchValue({ name: 'V', stateId: 'kl' });
    component.submit();
    expect(service.create).toHaveBeenCalledWith(expect.objectContaining({ countryId: 'in', stateId: 'kl', currencyId: 'cur' }));
  });

  it('edit keeps the stored links (even inactive) and posts them unchanged', () => {
    const { component, service } = mount('s1', supplier({ countryId: 'xx', countryName: 'Oldland', stateId: 'xs', stateName: 'Old Province' }));
    expect(component.form.controls.countryId.value).toBe('xx');
    expect(component.storedCountry()).toEqual({ id: 'xx', name: 'Oldland' });
    component.submit();
    expect(service.update).toHaveBeenCalledWith('s1', expect.objectContaining({ countryId: 'xx', stateId: 'xs' }));
  });
});
