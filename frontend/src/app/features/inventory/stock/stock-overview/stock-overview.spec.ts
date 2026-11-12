import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { StockOverview, locationText } from './stock-overview';
import { StockService } from '../stock.service';
import { AuthService } from '../../../../core/auth/auth.service';
import { ContextService } from '../../../../core/context/context.service';
import { StockLocationsService } from '../../../../shared/locations/stock-locations.service';
import type { StockLocation } from '../../../../shared/locations/locations.models';
import type { StockOverviewItem } from '../stock.models';

const loc = (id: string, name: string): StockLocation => ({
  id, code: id.toUpperCase(), name, locationType: 'BranchStore', branchId: 'b1', branchCode: 'MAIN', branchName: 'Main', isActive: true,
  canSell: true, canReceivePurchase: true, isSystemManaged: false,
});

const item = (o: Partial<StockOverviewItem> = {}): StockOverviewItem => ({
  productId: 'p', sku: 'PEN', name: 'Pen', category: null, baseUnitLabel: 'pc', baseDecimalPlaces: 0, quantityOnHand: 12.5, averageCost: 1, stockValue: 12.5,
  status: 'Normal', ...o,
});

function mount(opts: { view: boolean; locations: StockLocation[]; items?: StockOverviewItem[]; query?: Record<string, string> }) {
  const overview = vi.fn((..._a: unknown[]) => of({ items: opts.items ?? [item()], total: 1, page: 1, pageSize: 20 }));
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(opts.query ?? {}) } } },
      { provide: StockService, useValue: { overview } },
      { provide: StockLocationsService, useValue: { list: () => of(opts.locations) } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => opts.view && c === 'StockLocations.View' } },
      { provide: ContextService, useValue: { contextVersion: signal(0).asReadonly(), workingBranch: signal({ id: 'b1' }), companyPermissions: signal(new Set<string>()), companyViewActive: () => false, setCompanyView: () => undefined } },
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const f = TestBed.createComponent(StockOverview);
  f.detectChanges();
  return { f, el: f.nativeElement as HTMLElement, overview, navigate, c: f.componentInstance };
}

describe('StockOverview location filter', () => {
  it('shows the filter only with StockLocations.View and more than one location', () => {
    expect(mount({ view: true, locations: [loc('a', 'A'), loc('b', 'B')] }).el.querySelector('app-location-select select')).not.toBeNull();
    TestBed.resetTestingModule();
    expect(mount({ view: false, locations: [loc('a', 'A'), loc('b', 'B')] }).el.querySelector('app-location-select select')).toBeNull();
    TestBed.resetTestingModule();
    expect(mount({ view: true, locations: [loc('a', 'A')] }).el.querySelector('app-location-select select')).toBeNull();
  });

  it('passes the selected location to the overview call and resets to page 1; clearing sends none', () => {
    const { c, overview } = mount({ view: true, locations: [loc('a', 'A'), loc('b', 'B')] });
    expect(overview).toHaveBeenLastCalledWith(expect.objectContaining({ stockLocationId: null }));
    c.onLocationChange('b');
    expect(overview).toHaveBeenLastCalledWith(expect.objectContaining({ stockLocationId: 'b', page: 1 }));
    c.onLocationChange(null);
    expect(overview).toHaveBeenLastCalledWith(expect.objectContaining({ stockLocationId: null }));
  });

  it('applies the stockLocationId query param on init and keeps the URL in sync when the filter changes', () => {
    const { c, overview, navigate } = mount({ view: true, locations: [loc('a', 'A'), loc('b', 'B')], query: { stockLocationId: 'b' } });
    expect(c.locationId()).toBe('b');
    expect(overview).toHaveBeenCalledWith(expect.objectContaining({ stockLocationId: 'b' }));
    c.onLocationChange('a');
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { stockLocationId: 'a' }, queryParamsHandling: 'merge', replaceUrl: true }));
    c.onLocationChange(null);
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { stockLocationId: null } }));
  });

  it('renders the location column from locations[] and quantities without forcing 2 dp', () => {
    const { el } = mount({
      view: false, locations: [],
      items: [item({ locations: [{ locationId: 'l1', code: 'MAIN', name: 'Main', type: 'BranchStore', qty: 12.5, avgCost: 1, value: 12.5 }] })],
    });
    expect(el.textContent).toContain('MAIN');
    expect(el.textContent).toContain('12.5');
    expect(el.textContent).not.toContain('12.50');
  });

  it('summarises locations: none, one, several, transit', () => {
    const l = (code: string, type: 'BranchStore' | 'Transit') => ({ locationId: code, code, name: code, type, qty: 1, avgCost: 1, value: 1 });
    expect(locationText(item())).toBe('—');
    expect(locationText(item({ locations: [l('MAIN', 'BranchStore')] }))).toBe('MAIN');
    expect(locationText(item({ locations: [l('MAIN', 'BranchStore'), l('VAN', 'BranchStore')] }))).toBe('2 locations');
    expect(locationText(item({ locations: [l('TRN', 'Transit')] }))).toBe('TRN (in transit)');
  });
});

describe('StockOverview quantity precision', () => {
  const units = [
    { productUnitId: 'u-pc', unitCode: 'PC', label: 'Piece', factor: 1, isBaseUnit: true },
    { productUnitId: 'u-box', unitCode: 'BOX', label: 'Box', factor: 10, isBaseUnit: false },
    { productUnitId: 'u-fc', unitCode: 'FC', label: 'Full Carton', factor: 100, isBaseUnit: false },
  ];
  const qtyCell = (c: StockOverview, i: number) => c.rows()[i].cells[3] as { text: string; sub?: string };

  it('rows_pen122_showBreakdownAsSubText', () => {
    const { c, el } = mount({ view: false, locations: [], items: [item({ quantityOnHand: 122, breakdownUnits: units })] });
    expect(qtyCell(c, 0)).toMatchObject({ text: '122', sub: '1 Full Carton, 2 Box, 2 Piece' });
    expect(el.textContent).toContain('1 Full Carton, 2 Box, 2 Piece');
  });

  it('rows_kgProduct_usesBaseDecimalPlaces', () => {
    const { c } = mount({ view: false, locations: [], items: [item({ baseUnitLabel: 'kg', baseDecimalPlaces: 3, quantityOnHand: 12 })] });
    expect(qtyCell(c, 0).text).toBe('12.000');
  });

  it('rows_baseOnlyOrZero_haveNoSubText', () => {
    const { c } = mount({ view: false, locations: [], items: [item({ quantityOnHand: 5 }), item({ quantityOnHand: 0, breakdownUnits: units })] });
    expect(qtyCell(c, 0).sub).toBeUndefined();
    expect(qtyCell(c, 1).sub).toBeUndefined();
  });
});
