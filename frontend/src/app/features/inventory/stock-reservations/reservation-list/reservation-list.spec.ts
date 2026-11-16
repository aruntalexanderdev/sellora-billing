import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../../shared/scope/scope-testing';
import type { StockReservation } from '../../shipments/shipments.models';
import { ReservationsService } from '../reservations.service';
import { ReservationList } from './reservation-list';

const loc = (id: string) => ({ id, code: id.toUpperCase(), name: id, type: 'BranchStore' });
const res = (over: Partial<StockReservation> = {}): StockReservation => ({
  id: 'r1', status: 'Active', source: loc('store'), productId: 'p', productSku: 'PEN', productName: 'Pen', baseUnitCode: 'pc', baseQty: 50, shipmentId: 's1',
  shipmentNo: 'SHP-1', consignmentId: 'c1', consignmentNo: 'CON-1', consignmentLineId: 'cl1', destination: loc('van'), transferNo: 'TRF-1',
  createdAt: '2026-11-01T10:00:00Z', createdBy: 'admin', ageDays: 15, releasedAt: null, releasedBy: null, releaseReason: null, consumedAt: null, ...over,
});

function setup(permissions: string[], items: StockReservation[] = [res(), res({ id: 'r2', status: 'Released' })]) {
  const list = vi.fn(() => of({ items, total: items.length, page: 1, pageSize: 20 }));
  const release = vi.fn(() => of(res({ status: 'Released' })));
  TestBed.configureTestingModule({
    imports: [ReservationList],
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: ReservationsService, useValue: { list, release } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
    ],
  });
  const fixture = TestBed.createComponent(ReservationList);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, list, release };
}

describe('ReservationList', () => {
  it('shows the age and loads Active reservations by default', () => {
    const { el, list } = setup(['Shipments.View']);
    expect(el.querySelector('[data-testid="age"]')?.textContent).toContain('15 d');
    expect((list.mock.calls.at(-1) as unknown as [{ status: string }])[0].status).toBe('Active');
  });

  it('hides Release without Reservations.Release', () => {
    const { el } = setup(['Shipments.View']);
    expect(el.querySelector('[data-testid="release"]')).toBeNull();
  });

  it('offers Release only on Active rows for holders, and requires a reason', () => {
    const { fixture, el, release, component } = setup(['Shipments.View', 'Reservations.Release']);
    expect(el.querySelectorAll('[data-testid="release"]')).toHaveLength(1); // the Released row has none
    (el.querySelector('[data-testid="release"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelector('app-reason-prompt')).not.toBeNull();
    component.confirmRelease(null);
    expect(release).not.toHaveBeenCalled();
    component.confirmRelease('stale');
    expect(release).toHaveBeenCalledWith('r1', 'stale');
  });

  it('a non-holder cannot start a release programmatically', () => {
    const { component } = setup(['Shipments.View']);
    component.startRelease(res());
    expect(component.releasing()).toBeNull();
  });
});
