import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { ContextService } from '../../core/context/context.service';
import type { LocationPurpose, StockLocation } from './locations.models';
import { LocationSelect } from './location-select';
import { StockLocationsService } from './stock-locations.service';

const loc = (o: Partial<StockLocation> & Pick<StockLocation, 'id' | 'name'>): StockLocation => ({
  code: o.id.toUpperCase(), locationType: 'BranchStore', branchId: 'b1', branchCode: 'MAIN', branchName: 'Main', isActive: true,
  canSell: true, canReceivePurchase: true, isSystemManaged: false, ...o,
});

const STORE = loc({ id: 's-main', name: 'Main Store' });
const OTHER_STORE = loc({ id: 's-north', name: 'North Store', branchId: 'b2' });
const WAREHOUSE = loc({ id: 'w1', name: 'Back Warehouse', locationType: 'BranchWarehouse', canSell: false });
const VEHICLE = loc({ id: 'v1', name: 'Van', locationType: 'Vehicle', canReceivePurchase: false });
const TRANSIT = loc({ id: 't1', name: 'Transit', locationType: 'Transit', canSell: false, canReceivePurchase: false });
const INACTIVE = loc({ id: 'x1', name: 'Closed', isActive: false });

@Component({ imports: [LocationSelect], template: `<app-location-select [purpose]="purpose()" [branchId]="branch()" [(value)]="picked" />` })
class Host {
  readonly purpose = signal<LocationPurpose>('sale');
  readonly branch = signal<string | null>(null);
  readonly picked = signal<string | null>(null);
}

function mount(all: StockLocation[], opts: { view?: boolean; purpose?: LocationPurpose; branch?: string | null } = {}) {
  const list = vi.fn(() => of(all));
  TestBed.configureTestingModule({
    providers: [
      { provide: StockLocationsService, useValue: { list } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => (opts.view ?? true) && c === 'StockLocations.View' } },
      { provide: ContextService, useValue: { contextVersion: signal(0).asReadonly(), workingBranch: signal({ id: 'b1' }) } },
    ],
  });
  const fixture = TestBed.createComponent(Host);
  fixture.componentInstance.purpose.set(opts.purpose ?? 'sale');
  fixture.componentInstance.branch.set(opts.branch ?? null);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const names = () => Array.from(el.querySelectorAll('option')).map((o) => o.textContent?.trim());
  return { fixture, el, list, names, host: fixture.componentInstance };
}

describe('LocationSelect', () => {
  it('purpose=sale lists only CanSell locations (no warehouse, transit or inactive)', () => {
    const { names } = mount([STORE, WAREHOUSE, VEHICLE, TRANSIT, INACTIVE], { purpose: 'sale' });
    expect(names()).toEqual(['Main Store (S-MAIN)', 'Van (V1)']);
  });

  it('purpose=purchase lists only CanReceivePurchase locations', () => {
    const { names } = mount([STORE, WAREHOUSE, VEHICLE, TRANSIT], { purpose: 'purchase' });
    expect(names()).toEqual(['Main Store (S-MAIN)', 'Back Warehouse (W1)']);
  });

  it.each(['adjust', 'transfer'] as const)('purpose=%s lists every non-Transit location', (purpose) => {
    const { names } = mount([STORE, WAREHOUSE, VEHICLE, TRANSIT], { purpose });
    expect(names()).toEqual(['Main Store (S-MAIN)', 'Back Warehouse (W1)', 'Van (V1)']);
  });

  it('purpose=filter adds an "All locations" option and leaves the value null', () => {
    const { names, host } = mount([STORE, WAREHOUSE, TRANSIT], { purpose: 'filter' });
    expect(names()).toEqual(['All locations', 'Main Store (S-MAIN)', 'Back Warehouse (W1)']);
    expect(host.picked()).toBeNull();
  });

  it('defaults to the working branch store when several locations are eligible', () => {
    const { host } = mount([WAREHOUSE, OTHER_STORE, STORE], { purpose: 'adjust' });
    expect(host.picked()).toBe('s-main');
  });

  it('defaults to the document branch store when branchId is given', () => {
    const { host } = mount([STORE, OTHER_STORE], { purpose: 'sale', branch: 'b2' });
    expect(host.picked()).toBe('s-north');
  });

  it('passes the branch restriction to the service', () => {
    const { list } = mount([STORE, OTHER_STORE], { purpose: 'sale', branch: 'b2' });
    expect(list).toHaveBeenCalledWith({ branchId: 'b2' });
  });

  it('is hidden (and nothing is fetched) without StockLocations.View', () => {
    const { el, list, host } = mount([STORE, WAREHOUSE, VEHICLE], { view: false });
    expect(el.querySelector('select')).toBeNull();
    expect(list).not.toHaveBeenCalled();
    expect(host.picked()).toBeNull();
  });

  it('is hidden with a single eligible location and leaves the value null (server default)', () => {
    const { el, host } = mount([STORE, WAREHOUSE, TRANSIT], { purpose: 'sale' });
    expect(el.querySelector('select')).toBeNull();
    expect(host.picked()).toBeNull();
  });

  it('is hidden when nothing is eligible', () => {
    const { el } = mount([TRANSIT, INACTIVE], { purpose: 'sale' });
    expect(el.querySelector('select')).toBeNull();
  });

  it('writes the chosen location back to the model', () => {
    const { el, host, fixture } = mount([STORE, VEHICLE], { purpose: 'sale' });
    const sel = el.querySelector('select')!;
    sel.value = 'v1';
    sel.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(host.picked()).toBe('v1');
  });
});
