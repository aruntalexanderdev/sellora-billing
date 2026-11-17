import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../../shared/scope/scope-testing';
import { VehiclesService } from '../../../company/vehicles/vehicles.service';
import { ConsignmentsService } from '../../../inventory/consignments/consignments.service';
import { consignment, consignmentLine, shipment } from '../../../inventory/shipments/shipment-fixtures';
import { ShipmentsService } from '../../../inventory/shipments/shipments.service';
import { ProductsService } from '../../../inventory/products/products.service';
import { StockTransfersService } from '../../../inventory/stock-transfers/stock-transfers.service';
import { StockLocationsService } from '../../../../shared/locations/stock-locations.service';
import { dispatchedLine, line, transfer } from '../../../inventory/stock-transfers/test-fixtures';
import type { StockLocation } from '../../../../shared/locations/locations.models';
import { VanTransferWizard, type VanTransferMode } from './van-transfer-wizard';

const loc = (id: string, name: string, locationType: StockLocation['locationType']): StockLocation => ({
  id, code: id.toUpperCase(), name, locationType, branchId: 'b1', branchCode: 'B1', branchName: 'Branch 1', isActive: true, canSell: true, canReceivePurchase: true, isSystemManaged: false,
});
const unit = (id: string, unitCode: string, factor: number, isBaseUnit = false) => ({ id, unitCode, displayName: unitCode, decimalPlaces: 0, conversionFactorToBase: factor, isBaseUnit, isActive: true });

const ALL_PERMS = ['StockTransfers.Create', 'StockTransfers.Approve', 'Shipments.Plan', 'Shipments.Load', 'StockTransfers.Dispatch', 'StockTransfers.Receive', 'StockTransfers.View'];

function setup(mode: VanTransferMode, permissions: string[]) {
  const service = {
    create: vi.fn(() => of(transfer('Requested', [line()]))),
    approve: vi.fn(() => of(transfer('Approved', [line()]))),
    receive: vi.fn(() => of({ receiptId: 'r', transfer: transfer('Completed', [dispatchedLine({ outstandingBaseQty: 0 })]) })),
    submit: vi.fn(),
    get: vi.fn(() => of(transfer('Completed', [dispatchedLine({ outstandingBaseQty: 0 })]))),
  };
  // The shipment is already dispatched and fully accepted on the server: the chain only has to confirm that.
  const done = shipment('Dispatched', {
    dispatched: { userId: 'u', userName: 'admin', at: '2026-11-16T10:00:00Z' },
    vehicles: [{ id: 'sv1', sequence: 1, vehicleId: 'v1', vehicleNumber: 'V1', vehicleName: null, externalVehicleNumber: null, externalDescription: null, driverName: null, driverPhone: null }],
    consignments: [consignment({ status: 'InTransit', shipmentVehicleId: 'sv1', lines: [consignmentLine({ dispatchedBaseQty: 50, acceptedBaseQty: 50, loadedBaseQty: 50, loadedQty: 5 })] })],
  });
  const shipments = { create: vi.fn(() => of(done)), get: vi.fn(() => of(done)) };
  TestBed.configureTestingModule({
    imports: [VanTransferWizard],
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: StockTransfersService, useValue: service },
      { provide: ShipmentsService, useValue: shipments },
      { provide: ConsignmentsService, useValue: { receipts: () => of([]) } },
      { provide: VehiclesService, useValue: { list: () => of([]) } },
      { provide: StockLocationsService, useValue: { list: () => of([loc('store', 'Main Store', 'BranchStore'), loc('van', 'Van 1', 'Vehicle'), loc('transit', 'Transit', 'Transit'), loc('wh', 'Warehouse', 'BranchWarehouse')]) } },
      { provide: ProductsService, useValue: { list: () => of({ items: [{ id: 'pen', name: 'Pen', sku: 'P', isActive: true }], total: 1, page: 1, pageSize: 100 }), get: () => of({ id: 'pen', units: [unit('pc', 'pc', 1, true), unit('box', 'box', 10)] }) } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
    ],
  });
  const fixture = TestBed.createComponent(VanTransferWizard);
  fixture.componentRef.setInput('mode', mode);
  fixture.componentRef.setInput('vehicle', loc('van', 'Van 1', 'Vehicle'));
  fixture.detectChanges();
  const component = fixture.componentInstance;
  component.form.controls.lines.at(0).patchValue({ productId: 'pen', productUnitId: 'box', quantity: 5 });
  fixture.detectChanges();
  return { fixture, component, el: fixture.nativeElement as HTMLElement, service, shipments };
}

describe('VanTransferWizard', () => {
  it('offers only non-vehicle, non-transit counterparts and defaults to the branch store', () => {
    const { component } = setup('load', ['StockTransfers.Create']);
    expect(component.counterparts().map((l) => l.id)).toEqual(['store', 'wh']);
    expect(component.form.controls.counterpartId.value).toBe('store');
  });

  it('Load: counterpart -> vehicle; runs the whole chain (transfer, shipment, receipt) with every permission', async () => {
    const { component, service, shipments } = setup('load', ALL_PERMS);
    await component.run();
    expect(service.create).toHaveBeenCalledWith(expect.objectContaining({ sourceLocationId: 'store', destinationLocationId: 'van', submit: true }), expect.any(String));
    expect(shipments.create).toHaveBeenCalledWith(expect.objectContaining({ sourceLocationId: 'store', transportMode: 'Road', transportVia: 'OwnFleet' }), expect.any(String));
    expect(component.result()).toMatchObject({ stoppedAt: null, reason: null });
  });

  it('Unload: vehicle -> counterpart', async () => {
    const { component, service } = setup('unload', ['StockTransfers.Create']);
    component.form.controls.counterpartId.setValue('wh');
    await component.run();
    expect(service.create).toHaveBeenCalledWith(expect.objectContaining({ sourceLocationId: 'van', destinationLocationId: 'wh' }), expect.any(String));
  });

  it('stops at the first missing permission and links to the transfer', async () => {
    const { fixture, component, el, service } = setup('load', ['StockTransfers.Create', 'StockTransfers.View']);
    await component.run();
    fixture.detectChanges();
    expect(service.approve).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="wizard-stopped-permission"]')!.textContent).toContain('StockTransfers.Approve');
    const link = el.querySelector('[data-testid="wizard-transfer-link"]') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/inventory/stock-transfers/t1');
  });

  it('emits changed only when a step posted', async () => {
    const { component } = setup('load', ['StockTransfers.Create']);
    const changed = vi.fn();
    component.changed.subscribe(changed);
    await component.run();
    expect(changed).toHaveBeenCalledTimes(1);
  });

  it('retry after a network error resumes the same transfer with the same step key (no second create)', async () => {
    const { fixture, component, el, service } = setup('load', ['StockTransfers.Create', 'StockTransfers.Approve', 'StockTransfers.Receive', 'StockTransfers.View']);
    service.approve.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 0 })));

    await component.run();
    fixture.detectChanges();
    expect(component.result()).toMatchObject({ stoppedAt: 'approve', reason: 'error' });
    expect(el.querySelector('[data-testid="wizard-stopped-error"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="wizard-transfer-link"]')).not.toBeNull();

    await component.retry();
    fixture.detectChanges();
    expect(component.result()).toMatchObject({ stoppedAt: 'shipment', reason: 'permission', missingPermission: 'Shipments.Plan' });
    expect(service.create).toHaveBeenCalledTimes(1);
    expect(service.approve).toHaveBeenCalledTimes(2);
    expect(el.querySelector('[data-testid="wizard-stopped-permission"]')!.textContent).toContain('Shipments.Plan');
  });

  it('stops before the shipment step (not earlier, not later) for a user who can create and approve only, and links the transfer', async () => {
    const { fixture, component, el, service } = setup('unload', ['StockTransfers.Create', 'StockTransfers.Approve', 'StockTransfers.Receive', 'StockTransfers.View']);
    await component.run();
    fixture.detectChanges();
    expect(service.approve).toHaveBeenCalledTimes(1);
    expect(service.receive).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="wizard-stopped-permission"]')!.textContent).toContain('Shipments.Plan');
    expect((el.querySelector('[data-testid="wizard-transfer-link"]') as HTMLAnchorElement).getAttribute('href')).toBe('/inventory/stock-transfers/t1');
  });

  it('a fresh run after a completed chain uses new keys', async () => {
    const { component, service, shipments } = setup('load', ALL_PERMS);
    await component.run();
    await component.run();
    const key = (i: number) => (service.create.mock.calls[i] as unknown as unknown[])[1];
    expect(key(1)).not.toBe(key(0));
    const skey = (i: number) => (shipments.create.mock.calls[i] as unknown as unknown[])[1];
    expect(skey(1)).not.toBe(skey(0));
  });


});
