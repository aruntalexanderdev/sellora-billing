import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { ProductUnitsLookup } from '../../stock-transfers/product-units-lookup';
import { consignment, shipment } from '../shipment-fixtures';
import type { Shipment, ShipmentStatus } from '../shipments.models';
import { ShipmentsService } from '../shipments.service';
import { ShipmentDetail } from './shipment-detail';

const ALL = ['Shipments.View', 'Shipments.Plan', 'Shipments.Load', 'StockTransfers.Dispatch'];

function setup(s: Shipment, permissions: string[] = ALL, extra: Record<string, unknown> = {}) {
  const service = { get: vi.fn(() => of(s)), eligibleVehicles: vi.fn(() => of([])), dispatch: vi.fn((_id: string, _key: string) => of(shipment('Dispatched'))), ...extra };
  const ask = vi.fn(() => Promise.resolve(true));
  const show = vi.fn();
  TestBed.configureTestingModule({
    imports: [ShipmentDetail],
    providers: [
      provideRouter([]),
      { provide: ShipmentsService, useValue: service },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
      { provide: ConfirmDialogService, useValue: { ask } },
      { provide: ToastService, useValue: { show } },
      { provide: ProductUnitsLookup, useValue: { units: () => of([]) } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 's1' } } } },
    ],
  });
  const fixture = TestBed.createComponent(ShipmentDetail);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const has = (id: string) => el.querySelector(`[data-testid="${id}"]`) !== null;
  return { fixture, component: fixture.componentInstance, el, service, ask, show, has };
}

describe('ShipmentDetail dispatch', () => {
  it('shows Dispatch only for ReadyToDispatch with StockTransfers.Dispatch', () => {
    const cases: [ShipmentStatus, string[], boolean][] = [
      ['ReadyToDispatch', ALL, true],
      ['ReadyToDispatch', ['Shipments.View', 'Shipments.Plan', 'Shipments.Load'], false],
      ['Draft', ALL, false],
      ['Loading', ALL, false],
      ['Dispatched', ALL, false],
      ['PartiallyDelivered', ALL, false],
      ['Delivered', ALL, false],
      ['Cancelled', ALL, false],
    ];
    for (const [status, perms, shown] of cases) {
      TestBed.resetTestingModule();
      expect(setup(shipment(status), perms).has('act-dispatch'), `${status} ${perms.length}`).toBe(shown);
    }
  });

  it('hides cancel (shipment and consignment), edit and load once dispatched, and shows the stamp', () => {
    const dispatched = shipment('Dispatched', {
      dispatched: { userId: 'u', userName: 'dispatcher', at: '2026-11-16T10:00:00Z' },
      consignments: [consignment({ status: 'InTransit', dispatchedAt: '2026-11-16T10:00:00Z' })],
    });
    const { has, el } = setup(dispatched);
    expect(has('act-cancel')).toBe(false);
    expect(has('act-edit')).toBe(false);
    expect(has('cancel-cons-CON-0001')).toBe(false);
    expect(has('load-CON-0001')).toBe(false);
    expect(el.querySelector('[data-testid="dispatched-stamp"]')?.textContent).toContain('dispatcher');
    expect(el.querySelector('[data-testid="cons-dispatched"]')).not.toBeNull();
    expect(el.querySelector('app-shipment-allocate-panel')).toBeNull();
  });

  it('still offers cancel on ReadyToDispatch', () => {
    expect(setup(shipment('ReadyToDispatch')).has('act-cancel')).toBe(true);
  });

  it('confirms, then dispatches with an idempotency key and shows the new state', async () => {
    const { fixture, el, service, ask, show, component } = setup(shipment('ReadyToDispatch'));
    (el.querySelector('[data-testid="act-dispatch"] button') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(ask).toHaveBeenCalledTimes(1);
    expect(service.dispatch).toHaveBeenCalledWith('s1', expect.any(String));
    expect(component.shipment()!.status).toBe('Dispatched');
    expect(show).toHaveBeenCalled();
    expect(el.querySelector('[data-testid="act-dispatch"]')).toBeNull();
  });

  it('does nothing when the confirmation is declined', async () => {
    const { fixture, el, service, ask } = setup(shipment('ReadyToDispatch'));
    ask.mockResolvedValueOnce(false);
    (el.querySelector('[data-testid="act-dispatch"] button') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(service.dispatch).not.toHaveBeenCalled();
  });

  it('shows the server problem and keeps the same key for a retry after a network error', async () => {
    const keys: string[] = [];
    const responses = [
      () => throwError(() => new HttpErrorResponse({ status: 0 })),
      () => throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { status: ['Load every consignment first.'] } } })),
    ];
    const dispatch = vi.fn((_id: string, key: string) => {
      keys.push(key);
      return responses[keys.length - 1]();
    });
    const { fixture, el, component } = setup(shipment('ReadyToDispatch'), ALL, { dispatch });
    const click = async () => {
      (el.querySelector('[data-testid="act-dispatch"] button') as HTMLButtonElement).click();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    await click();
    await click();
    expect(keys[1]).toBe(keys[0]);
    expect(el.querySelector('[data-testid="action-error"]')?.textContent).toContain('Load every consignment first.');
    expect(component.shipment()!.status).toBe('ReadyToDispatch');
  });
});
