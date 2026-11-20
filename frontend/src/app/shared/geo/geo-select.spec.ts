import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { of } from 'rxjs';
import { GeoSelect } from './geo-select';
import type { GeoLookup, GeoStoredLink } from './geo.models';

const COUNTRIES = [{ id: 'in', code: 'IN', name: 'India' }, { id: 'ae', code: 'AE', name: 'United Arab Emirates' }];
const STATES: Record<string, { id: string; countryId: string; code: string; name: string; gstStateCode: string | null }[]> = {
  in: [{ id: 'kl', countryId: 'in', code: 'KL', name: 'Kerala', gstStateCode: '32' }, { id: 'tn', countryId: 'in', code: 'TN', name: 'Tamil Nadu', gstStateCode: '33' }],
  ae: [{ id: 'du', countryId: 'ae', code: 'DU', name: 'Dubai', gstStateCode: null }],
};

@Component({
  standalone: true,
  imports: [GeoSelect],
  template: `<app-geo-select [countryControl]="country" [stateControl]="state" [lookup]="lookup" [storedCountry]="storedCountry" [storedState]="storedState" [legacyState]="legacy" />`,
})
class Host {
  country = new FormControl('', { nonNullable: true });
  state = new FormControl('', { nonNullable: true });
  lookup: GeoLookup = {
    lookupCountries: vi.fn(() => of(COUNTRIES)),
    lookupStates: vi.fn((id: string) => of(STATES[id] ?? [])),
  };
  storedCountry: GeoStoredLink | null = null;
  storedState: GeoStoredLink | null = null;
  legacy: string | null = null;
}

function mount(setup: (h: Host) => void = () => undefined) {
  TestBed.configureTestingModule({ imports: [Host] });
  const fixture = TestBed.createComponent(Host);
  setup(fixture.componentInstance);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const opts = (testid: string) => Array.from(el.querySelectorAll<HTMLOptionElement>(`[data-testid="${testid}"] option`)).map((o) => o.textContent?.trim());
  const pick = (testid: string, value: string) => {
    const sel = el.querySelector<HTMLSelectElement>(`[data-testid="${testid}"]`)!;
    sel.value = value;
    sel.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  };
  return { fixture, host: fixture.componentInstance, el, opts, pick };
}

describe('GeoSelect (country -> state pair)', () => {
  it('lists active countries and asks for a country before offering states', () => {
    const { opts } = mount();
    expect(opts('geo-country')).toEqual(['Select a country…', 'India', 'United Arab Emirates']);
    expect(opts('geo-state')).toEqual(['Select a country first']);
  });

  it('filters states by the selected country', () => {
    const { opts, pick, host } = mount();
    pick('geo-country', 'in');
    expect(host.lookup.lookupStates).toHaveBeenCalledWith('in');
    expect(opts('geo-state')).toEqual(['Select a state…', 'Kerala', 'Tamil Nadu']);
    pick('geo-country', 'ae');
    expect(opts('geo-state')).toEqual(['Select a state…', 'Dubai']);
  });

  it('changing the country clears a chosen state, and clearing the country empties the states', () => {
    const { pick, host, opts } = mount();
    pick('geo-country', 'in');
    pick('geo-state', 'kl');
    expect(host.state.value).toBe('kl');
    pick('geo-country', 'ae');
    expect(host.state.value).toBe('');
    expect(host.state.dirty).toBe(true);
    pick('geo-country', '');
    expect(host.country.value).toBe('');
    expect(opts('geo-state')).toEqual(['Select a country first']);
  });

  it('keeps an existing inactive country and state selectable and displayed (BR5, AC6)', () => {
    const { opts, host, el } = mount((h) => {
      h.storedCountry = { id: 'xx', name: 'Oldland' };
      h.storedState = { id: 'xs', name: 'Old Province' };
      h.country.setValue('xx');
      h.state.setValue('xs');
    });
    expect(opts('geo-country')).toContain('Oldland');
    expect(opts('geo-state')).toContain('Old Province');
    expect(el.querySelector<HTMLSelectElement>('[data-testid="geo-country"]')!.selectedOptions[0].textContent).toContain('Oldland');
    expect(el.querySelector<HTMLSelectElement>('[data-testid="geo-state"]')!.selectedOptions[0].textContent).toContain('Old Province');
    expect(host.state.value).toBe('xs');
  });

  it('does not inject the stored state under a different country', () => {
    const { opts, pick } = mount((h) => {
      h.storedCountry = { id: 'in', name: 'India' };
      h.storedState = { id: 'old', name: 'Old Province' };
      h.country.setValue('in');
    });
    expect(opts('geo-state')).toContain('Old Province');
    pick('geo-country', 'ae');
    expect(opts('geo-state')).not.toContain('Old Province');
  });

  it('shows the legacy text as a read-only hint only while no state is linked (D6)', () => {
    const { el, pick, fixture } = mount((h) => { h.legacy = 'Kerla'; });
    expect(el.querySelector('[data-testid="geo-legacy"]')?.textContent).toContain('Kerla');
    pick('geo-country', 'in');
    pick('geo-state', 'kl');
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="geo-legacy"]')).toBeNull();
  });

  it('shows server errors on the matching select', () => {
    const { el, host, fixture } = mount();
    host.state.setErrors({ server: 'State does not belong to the country.' });
    fixture.detectChanges();
    expect(el.textContent).toContain('State does not belong to the country.');
  });
});
