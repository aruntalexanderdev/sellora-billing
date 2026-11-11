import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../../shared/scope/scope-testing';
import { StockService } from '../../../inventory/stock/stock.service';
import { StockLocationsService } from '../../../../shared/locations/stock-locations.service';
import type { StockLocation } from '../../../../shared/locations/locations.models';
import { VanWorkspace } from './van-workspace';

const vehicle = (id: string): StockLocation => ({
  id, code: id.toUpperCase(), name: `Van ${id}`, locationType: 'Vehicle', branchId: 'b1', branchCode: 'B1', branchName: 'Branch 1', isActive: true, canSell: true, canReceivePurchase: false, isSystemManaged: false,
});

function setup(vehicles: StockLocation[], permissions: string[], query: Record<string, string> = {}) {
  const list = vi.fn(() => of(vehicles));
  const overview = vi.fn(() => of({ items: [{ productId: 'pen', sku: 'PEN-1', name: 'Pen', category: null, baseUnitLabel: 'pc', quantityOnHand: 120, averageCost: 1, stockValue: 120, status: 'InStock' }], total: 1, page: 1, pageSize: 50 }));
  TestBed.configureTestingModule({
    imports: [VanWorkspace],
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
      { provide: StockLocationsService, useValue: { list } },
      { provide: StockService, useValue: { overview } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(VanWorkspace);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, list, overview, navigate };
}

describe('VanWorkspace', () => {
  it('lists the working branch vehicles and preselects the only one, showing its stock filtered by that location', () => {
    const { fixture, el, list, overview, component } = setup([vehicle('v1')], ['StockLocations.View', 'Inventory.View']);
    fixture.detectChanges();
    expect(list).toHaveBeenCalledWith(expect.objectContaining({ type: 'Vehicle' }));
    expect(component.vehicleId()).toBe('v1');
    expect(overview).toHaveBeenCalledWith(expect.objectContaining({ stockLocationId: 'v1' }));
    expect(el.querySelector('[data-testid="vehicle-stock"]')!.textContent).toContain('120 pc');
    expect(el.querySelector('[data-testid="full-stock-link"]')!.getAttribute('href')).toContain('stockLocationId=v1');
  });

  it('honours ?vehicleId and requires a choice when there are several vehicles', () => {
    expect(setup([vehicle('v1'), vehicle('v2')], ['StockLocations.View'], {}).component.vehicleId()).toBeNull();
    TestBed.resetTestingModule();
    expect(setup([vehicle('v1'), vehicle('v2')], ['StockLocations.View'], { vehicleId: 'v2' }).component.vehicleId()).toBe('v2');
  });

  it('Sell from this vehicle opens POS with the vehicle in the query', () => {
    const { component, navigate } = setup([vehicle('v1')], ['StockLocations.View']);
    component.sell();
    expect(navigate).toHaveBeenCalledWith(['/sales/new'], { queryParams: { locationId: 'v1' } });
  });

  it('shows Load / Unload only with StockTransfers.Create', () => {
    const noTransfer = setup([vehicle('v1')], ['StockLocations.View']);
    expect(noTransfer.el.querySelector('[data-testid="load-vehicle"]')).toBeNull();
    TestBed.resetTestingModule();
    const withTransfer = setup([vehicle('v1')], ['StockLocations.View', 'StockTransfers.Create']);
    expect(withTransfer.el.querySelector('[data-testid="load-vehicle"]')).not.toBeNull();
    expect(withTransfer.el.querySelector('[data-testid="unload-vehicle"]')).not.toBeNull();
  });

  it('explains when the caller cannot list locations', () => {
    const { el, list } = setup([], []);
    expect(list).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="no-permission"]')).not.toBeNull();
  });
});
