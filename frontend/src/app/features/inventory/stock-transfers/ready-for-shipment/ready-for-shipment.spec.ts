import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../../shared/scope/scope-testing';
import { ReadyForShipment } from './ready-for-shipment';
import { StockTransfersService } from '../stock-transfers.service';

const row = { id: 't1', transferNo: 'TRF-0009', status: 'PartiallyFulfilled', sourceLocationId: 's', sourceLocationName: 'Main Store', destinationLocationId: 'd', destinationLocationName: 'Van 1', destinationBranchName: 'North', approvedAt: '2026-11-06T10:00:00Z', lineCount: 3, remainingLineCount: 2 };

function setup() {
  const readyForShipment = vi.fn(() => of({ items: [row], total: 1, page: 1, pageSize: 20 }));
  TestBed.configureTestingModule({
    imports: [ReadyForShipment],
    providers: [provideRouter([]), ...provideScopeStubs(), { provide: StockTransfersService, useValue: { readyForShipment } }, { provide: AuthService, useValue: { hasPermission: () => true } }],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(ReadyForShipment);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, readyForShipment, navigate };
}

describe('ReadyForShipment', () => {
  it('loads page 1 with the default page size and renders status label, destination branch and remaining lines', () => {
    const { el, readyForShipment } = setup();
    expect(readyForShipment).toHaveBeenCalledWith({ sourceLocationId: null, search: undefined, page: 1, pageSize: 20 });
    const text = el.textContent ?? '';
    expect(text).toContain('TRF-0009');
    expect(text).toContain('Partially fulfilled');
    expect(text).toContain('North');
    expect(text).toContain('2 of 3');
  });

  it('reloads from page 1 with the source filter and search', () => {
    const { component, readyForShipment } = setup();
    component.onPageChange(2);
    component.onSourceChange('s');
    expect(readyForShipment).toHaveBeenLastCalledWith({ sourceLocationId: 's', search: undefined, page: 1, pageSize: 20 });
    component.onSearch('TRF');
    expect(readyForShipment).toHaveBeenLastCalledWith({ sourceLocationId: 's', search: 'TRF', page: 1, pageSize: 20 });
  });

  it('reloads when the Company view is toggled (the service adds scope=company)', () => {
    const { component, readyForShipment } = setup();
    const calls = readyForShipment.mock.calls.length;
    component.onCompanyViewChange();
    expect(readyForShipment.mock.calls.length).toBe(calls + 1);
  });
});
