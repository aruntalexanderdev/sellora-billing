import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { ToastService } from '../ui/toast/toast.service';
import { GeoCountriesScreen } from './geo-countries-screen';
import { GeoStatesScreen } from './geo-states-screen';
import type { CountryDto, GeographyApi, StateDto } from './geo.models';

const country = (o: Partial<CountryDto> = {}): CountryDto => ({ id: 'c1', code: 'IN', iso3Code: 'IND', name: 'India', isActive: true, isSystem: true, ...o });
const stateRow = (o: Partial<StateDto> = {}): StateDto => ({
  id: 's1', countryId: 'c1', countryCode: 'IN', countryName: 'India', code: 'KL', name: 'Kerala', kind: 'State', gstStateCode: '32', isActive: true, isSystem: true, ...o,
});

function api(canEdit: boolean, over: Partial<GeographyApi> = {}): GeographyApi {
  return {
    listCountries: vi.fn(() => of({ canEdit, items: [country(), country({ id: 'c2', code: 'XX', iso3Code: null, name: 'Oldland', isActive: false, isSystem: false })] })),
    createCountry: vi.fn(() => of(country())),
    updateCountry: vi.fn(() => of(country())),
    setCountryActive: vi.fn(() => of(undefined)),
    listStates: vi.fn(() => of({ canEdit, items: [stateRow(), stateRow({ id: 's2', code: 'OLD', name: 'Old', isActive: false, isSystem: false, gstStateCode: null })] })),
    createState: vi.fn(() => of(stateRow())),
    updateState: vi.fn(() => of(stateRow())),
    setStateActive: vi.fn(() => of(undefined)),
    lookupCountries: vi.fn(() => of([{ id: 'c1', code: 'IN', name: 'India' }, { id: 'c3', code: 'AE', name: 'UAE' }])),
    ...over,
  };
}

function mount<T>(cmp: new () => T, a: GeographyApi, allowWrite = true) {
  const toast = { show: vi.fn() };
  TestBed.configureTestingModule({ imports: [cmp as never], providers: [{ provide: ToastService, useValue: toast }] });
  const fixture = TestBed.createComponent(cmp);
  fixture.componentRef.setInput('api', a);
  fixture.componentRef.setInput('allowWrite', allowWrite);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const click = (text: string) => {
    const b = Array.from(el.querySelectorAll<HTMLButtonElement>('button')).find((x) => x.textContent?.trim() === text);
    expect(b, `button "${text}"`).toBeTruthy();
    b!.click();
    fixture.detectChanges();
  };
  const fill = (sel: string, value: string, ev = 'input') => {
    const n = el.querySelector<HTMLInputElement>(sel)!;
    n.value = value;
    n.dispatchEvent(new Event(ev));
    fixture.detectChanges();
  };
  const submit = () => {
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  };
  return { fixture, el, toast, click, fill, submit, component: fixture.componentInstance };
}

describe('Countries screen', () => {
  it('canEdit=false: read-only (AC8) with no write actions and the platform-managed note', () => {
    const { el } = mount(GeoCountriesScreen, api(false));
    expect(el.querySelector('[data-testid="read-only-note"]')?.textContent).toContain('managed by the platform administrator');
    expect(el.textContent).not.toMatch(/\+ New|Edit|Deactivate|Activate\b/);
    expect(el.querySelectorAll('tbody tr')).toHaveLength(2);
  });

  it('canEdit=true but no Masters.Edit permission: still read-only', () => {
    const { el } = mount(GeoCountriesScreen, api(true), false);
    expect(el.textContent).not.toMatch(/\+ New|Deactivate/);
  });

  it('requests inactive rows by default and re-queries when the toggle is switched off (D20)', () => {
    const a = api(true);
    const { el, fixture } = mount(GeoCountriesScreen, a);
    expect(a.listCountries).toHaveBeenLastCalledWith(true);
    expect(el.textContent).toContain('Inactive');
    const t = el.querySelector<HTMLInputElement>('[data-testid="show-inactive"]')!;
    t.checked = false;
    t.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(a.listCountries).toHaveBeenLastCalledWith(false);
  });

  it('create posts upper-cased codes and the active flag', () => {
    const a = api(true);
    const { click, fill, submit, toast } = mount(GeoCountriesScreen, a);
    click('+ New country');
    fill('#co-code', 'ae');
    fill('#co-iso3', 'are');
    fill('#co-name', ' United Arab Emirates ');
    submit();
    expect(a.createCountry).toHaveBeenCalledWith({ code: 'AE', iso3Code: 'ARE', name: 'United Arab Emirates', isActive: true });
    expect(toast.show).toHaveBeenCalledWith('Country created.');
  });

  it('editing a system country locks the codes but still posts them unchanged', () => {
    const a = api(true);
    const { click, el, fill, submit } = mount(GeoCountriesScreen, a);
    click('Edit');
    expect(el.querySelector<HTMLInputElement>('#co-code')!.disabled).toBe(true);
    expect(el.querySelector<HTMLInputElement>('#co-iso3')!.disabled).toBe(true);
    fill('#co-name', 'Republic of India');
    submit();
    expect(a.updateCountry).toHaveBeenCalledWith('c1', { code: 'IN', iso3Code: 'IND', name: 'Republic of India' });
  });

  it('activate/deactivate calls the status endpoint', () => {
    const a = api(true);
    const { click } = mount(GeoCountriesScreen, a);
    click('Deactivate');
    expect(a.setCountryActive).toHaveBeenCalledWith('c1', false);
    click('Activate');
    expect(a.setCountryActive).toHaveBeenCalledWith('c2', true);
  });

  it('maps server field errors onto the controls', () => {
    const a = api(true, { createCountry: vi.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: { status: 400, errors: { code: ['Code already used.'] } } }))) });
    const { click, fill, submit, el } = mount(GeoCountriesScreen, a);
    click('+ New country');
    fill('#co-code', 'ae');
    fill('#co-name', 'UAE');
    submit();
    expect(el.querySelector('#co-code')!.parentElement!.textContent).toContain('Code already used.');
  });
});

describe('States screen', () => {
  it('canEdit=false: read-only (AC8)', () => {
    const { el } = mount(GeoStatesScreen, api(false));
    expect(el.querySelector('[data-testid="read-only-note"]')).not.toBeNull();
    expect(el.textContent).not.toMatch(/\+ New|Edit|Deactivate|Activate\b/);
  });

  it('shows inactive states and filters by country', () => {
    const a = api(true);
    const { el, fixture } = mount(GeoStatesScreen, a);
    expect(a.listStates).toHaveBeenLastCalledWith(null, true);
    const f = el.querySelector<HTMLSelectElement>('[data-testid="country-filter"]')!;
    f.value = 'c1';
    f.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(a.listStates).toHaveBeenLastCalledWith('c1', true);
  });

  it('create offers all six kinds, shows GST only for India and posts the request', () => {
    const a = api(true);
    const { click, fill, submit, el } = mount(GeoStatesScreen, a);
    click('+ New state');
    expect(Array.from(el.querySelectorAll('#st-kind option')).map((o) => o.textContent?.trim())).toEqual(
      ['State', 'Union territory', 'Emirate', 'Region', 'Governorate', 'Municipality'],
    );
    expect(el.querySelector('#st-gst')).toBeNull();
    fill('#st-country', 'c1', 'change');
    expect(el.querySelector('#st-gst')).not.toBeNull();
    fill('#st-code', 'gj');
    fill('#st-name', 'Gujarat');
    fill('#st-gst', '24');
    submit();
    expect(a.createState).toHaveBeenCalledWith({ countryId: 'c1', code: 'GJ', name: 'Gujarat', kind: 'State', gstStateCode: '24', isActive: true });
  });

  it('non-India create never posts a GST code', () => {
    const a = api(true);
    const { click, fill, submit } = mount(GeoStatesScreen, a);
    click('+ New state');
    fill('#st-country', 'c3', 'change');
    fill('#st-code', 'DU');
    fill('#st-name', 'Dubai');
    fill('#st-kind', 'Emirate', 'change');
    submit();
    expect(a.createState).toHaveBeenCalledWith({ countryId: 'c3', code: 'DU', name: 'Dubai', kind: 'Emirate', gstStateCode: null, isActive: true });
  });

  it('edit: country is fixed, system code and GST are locked, update has no countryId', () => {
    const a = api(true);
    const { click, el, fill, submit } = mount(GeoStatesScreen, a);
    click('Edit');
    expect(el.querySelector<HTMLSelectElement>('#st-country')!.disabled).toBe(true);
    expect(el.querySelector<HTMLInputElement>('#st-code')!.disabled).toBe(true);
    expect(el.querySelector<HTMLInputElement>('#st-gst')!.disabled).toBe(true);
    fill('#st-name', 'Kerala State');
    submit();
    expect(a.updateState).toHaveBeenCalledWith('s1', { code: 'KL', name: 'Kerala State', kind: 'State', gstStateCode: '32' });
  });

  it('activating a state under an inactive country surfaces the server isActive message', () => {
    const a = api(true, { setStateActive: vi.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: { status: 400, errors: { isActive: ['Country is inactive.'] } } }))) });
    const { click, toast } = mount(GeoStatesScreen, a);
    click('Activate');
    expect(toast.show).toHaveBeenCalledWith('Country is inactive.');
  });
});
