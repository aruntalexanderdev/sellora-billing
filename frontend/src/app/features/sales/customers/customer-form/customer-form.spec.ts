import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { GeoService } from '../../../../shared/geo/geo.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { SettingsService } from '../../../masters/settings/settings.service';
import type { CustomerItem } from '../customers.models';
import { CustomersService } from '../customers.service';
import { CustomerForm } from './customer-form';

const customer = (o: Partial<CustomerItem> = {}): CustomerItem => ({
  id: 'c1', name: 'Acme', phone: null, address: null, isWalkIn: false, isActive: true, countryId: null, countryName: null, stateId: null, stateName: null, ...o,
});

function mount(id: string | null, loaded = customer(), settingsCountry: string | null = 'in', canViewSettings = true) {
  const service = { get: vi.fn(() => of(loaded)), create: vi.fn(() => of(customer())), update: vi.fn(() => of(customer())) };
  const settings = { get: vi.fn(() => of({ countryId: settingsCountry })) };
  TestBed.configureTestingModule({
    imports: [CustomerForm],
    providers: [
      { provide: CustomersService, useValue: service },
      { provide: SettingsService, useValue: settings },
      { provide: AuthService, useValue: { hasPermission: () => canViewSettings } },
      { provide: GeoService, useValue: { lookupCountries: () => of([{ id: 'in', code: 'IN', name: 'India' }]), lookupStates: () => of([]) } },
      { provide: Router, useValue: { navigateByUrl: vi.fn() } },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } } },
    ],
  });
  const fixture = TestBed.createComponent(CustomerForm);
  fixture.detectChanges();
  return { component: fixture.componentInstance, service, settings, fixture };
}

describe('CustomerForm country/state', () => {
  it('new customer defaults to the Business Settings country with no state (D7)', () => {
    const { component } = mount(null);
    expect(component.form.controls.countryId.value).toBe('in');
    expect(component.form.controls.stateId.value).toBe('');
  });

  it('does not query settings without Masters.View, nor for edits', () => {
    expect(mount(null, customer(), 'in', false).settings.get).not.toHaveBeenCalled();
    TestBed.resetTestingModule();
    expect(mount('c1').settings.get).not.toHaveBeenCalled();
  });

  it('the default can be changed and the payload carries countryId/stateId', () => {
    const { component, service } = mount(null);
    component.form.patchValue({ name: 'New', countryId: 'ae', stateId: 'du' });
    component.submit();
    expect(service.create).toHaveBeenCalledWith(expect.objectContaining({ name: 'New', countryId: 'ae', stateId: 'du' }));
  });

  it('edit loads stored links and posts them back; clearing the country posts nulls (D22)', () => {
    const { component, service } = mount('c1', customer({ countryId: 'in', countryName: 'India', stateId: 'kl', stateName: 'Kerala' }));
    expect(component.storedState()).toEqual({ id: 'kl', name: 'Kerala' });
    component.submit();
    expect(service.update).toHaveBeenLastCalledWith('c1', expect.objectContaining({ countryId: 'in', stateId: 'kl' }));
    component.form.patchValue({ countryId: '' });
    component.submit();
    expect(service.update).toHaveBeenLastCalledWith('c1', expect.objectContaining({ countryId: null, stateId: null }));
  });
});
