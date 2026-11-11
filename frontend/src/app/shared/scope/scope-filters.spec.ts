import { HttpTestingController } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuthService } from '../../core/auth/auth.service';
import type { StockLocation } from '../locations/locations.models';
import { StockLocationsService } from '../locations/stock-locations.service';
import { ScopeFilters } from './scope-filters';
import { provideScopeStubs } from './scope-testing';

const BRANCHES_URL = `${environment.apiBaseUrl}/branches/accessible`;
const b = (id: string, name: string, code: string) => ({ id, name, code, isDefault: false });
const loc = (id: string, name: string, over: Partial<StockLocation> = {}): StockLocation => ({
  id, code: id.toUpperCase(), name, locationType: 'BranchStore', branchId: 'b1', branchCode: 'B1', branchName: 'Branch 1', isActive: true,
  canSell: true, canReceivePurchase: true, isSystemManaged: false, ...over,
});

@Component({ imports: [ScopeFilters], template: `<app-scope-filters [(branchId)]="branch" [(stockLocationId)]="location" />` })
class Host {
  readonly branch = signal<string | null>(null);
  readonly location = signal<string | null>(null);
}

function mount(opts: { view?: boolean; locations?: StockLocation[] } = {}) {
  TestBed.configureTestingModule({
    providers: [
      ...provideScopeStubs(),
      { provide: StockLocationsService, useValue: { list: vi.fn(() => of(opts.locations ?? [])) } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => (opts.view ?? true) && c === 'StockLocations.View' } },
    ],
  });
  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();
  const http = TestBed.inject(HttpTestingController);
  const el = fixture.nativeElement as HTMLElement;
  return { fixture, http, el, branchSelect: () => el.querySelector('[data-testid="branch-filter"] select'), locationSelect: () => el.querySelector('[data-testid="location-filter"] select') };
}

describe('ScopeFilters (filter bar)', () => {
  it('hides the location filter with a single eligible location', () => {
    const m = mount({ locations: [loc('s1', 'Main Store')] });
    m.http.expectOne(BRANCHES_URL).flush([b('b1', 'Main', 'MAIN'), b('b2', 'North', 'NTH')]);
    m.fixture.detectChanges();
    expect(m.locationSelect()).toBeNull();
  });

  it('hides the location filter when the only extra locations are Transit or inactive (not eligible)', () => {
    const m = mount({ locations: [loc('s1', 'Main Store'), loc('t1', 'Transit', { locationType: 'Transit' }), loc('x1', 'Closed', { isActive: false })] });
    m.http.expectOne(BRANCHES_URL).flush([b('b1', 'Main', 'MAIN')]);
    m.fixture.detectChanges();
    expect(m.locationSelect()).toBeNull();
  });

  it('hides the location filter without StockLocations.View even with many locations', () => {
    const m = mount({ view: false, locations: [loc('s1', 'Main Store'), loc('w1', 'Warehouse', { locationType: 'BranchWarehouse' })] });
    m.http.expectOne(BRANCHES_URL).flush([b('b1', 'Main', 'MAIN')]);
    m.fixture.detectChanges();
    expect(m.locationSelect()).toBeNull();
  });

  it('shows the location filter (with an All locations option) with more than one eligible location', () => {
    const m = mount({ locations: [loc('s1', 'Main Store'), loc('w1', 'Warehouse', { locationType: 'BranchWarehouse' })] });
    m.http.expectOne(BRANCHES_URL).flush([b('b1', 'Main', 'MAIN')]);
    m.fixture.detectChanges();
    const options = Array.from(m.locationSelect()!.querySelectorAll('option')).map((o) => o.textContent?.trim());
    expect(options).toEqual(['All locations', 'Main Store (S1)', 'Warehouse (W1)']);
  });

  it('hides the branch filter with one accessible branch and shows it with two', () => {
    const one = mount();
    one.http.expectOne(BRANCHES_URL).flush([b('b1', 'Main', 'MAIN')]);
    one.fixture.detectChanges();
    expect(one.branchSelect()).toBeNull();
    TestBed.resetTestingModule();

    const two = mount();
    two.http.expectOne(BRANCHES_URL).flush([b('b1', 'Main', 'MAIN'), b('b2', 'North', 'NTH')]);
    two.fixture.detectChanges();
    expect(two.branchSelect()).not.toBeNull();
  });

  it('hides the branch filter when the branch list cannot be loaded', () => {
    const m = mount();
    m.http.expectOne(BRANCHES_URL).flush({}, { status: 500, statusText: 'Server Error' });
    m.fixture.detectChanges();
    expect(m.branchSelect()).toBeNull();
  });
});
