import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import type { StockLocation } from '../../../../shared/locations/locations.models';
import { StockLocationsService } from '../../../../shared/locations/stock-locations.service';
import { StockLocationList } from './stock-location-list';

const loc = (o: Partial<StockLocation> = {}): StockLocation => ({
  id: 'l1', code: 'NTH-STORE', name: 'North store', locationType: 'BranchStore', branchId: 'b1', branchCode: 'NTH', branchName: 'North',
  isActive: true, canSell: true, canReceivePurchase: true, isSystemManaged: true, ...o,
});

function setup() {
  const service = { list: vi.fn(() => of([loc(), loc({ id: 'l2', locationType: 'Transit', canSell: false, canReceivePurchase: false, branchId: null })])) };
  TestBed.configureTestingModule({ imports: [StockLocationList], providers: [{ provide: StockLocationsService, useValue: service }] });
  return { component: TestBed.createComponent(StockLocationList).componentInstance, service };
}

describe('StockLocationList', () => {
  it('loads and shows CanSell / CanReceivePurchase as Yes/No', () => {
    const { component } = setup();
    component.ngOnInit();
    expect(component.state()).toBe('ready');
    const cell = (r: number, i: number) => {
      const c = component.rows()[r].cells[i];
      return c.kind === 'badge' ? c.text : '';
    };
    expect([cell(0, 4), cell(0, 5), cell(1, 4), cell(1, 5)]).toEqual(['Yes', 'Yes', 'No', 'No']);
  });

  it('passes type, branch and includeInactive filters', () => {
    const { component, service } = setup();
    component.setType('Vehicle');
    component.setBranch('b1');
    component.setIncludeInactive(true);
    expect(service.list).toHaveBeenLastCalledWith({ type: 'Vehicle', branchId: 'b1', includeInactive: true });
  });

  it('ignores an unknown type value and has no mutating actions', () => {
    const { component } = setup();
    component.setType('bogus');
    component.ngOnInit();
    expect(component.type()).toBe('');
    expect(component.rows()[0].cells.some((c) => c.kind === 'actions')).toBe(false);
  });

  it('error state on failure', () => {
    const { component, service } = setup();
    service.list.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.ngOnInit();
    expect(component.state()).toBe('error');
  });
});
