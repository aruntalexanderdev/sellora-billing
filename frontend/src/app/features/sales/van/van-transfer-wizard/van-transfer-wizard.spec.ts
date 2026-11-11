import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../../shared/scope/scope-testing';
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

function setup(mode: VanTransferMode, permissions: string[]) {
  const service = {
    create: vi.fn(() => of(transfer('Requested', [line()]))),
    approve: vi.fn(() => of(transfer('Approved', [line()]))),
    dispatch: vi.fn(() => of(transfer('Dispatched', [dispatchedLine()]))),
    receive: vi.fn(() => of({ receiptId: 'r', transfer: transfer('Completed', [dispatchedLine({ outstandingBaseQty: 0 })]) })),
    submit: vi.fn(),
  };
  TestBed.configureTestingModule({
    imports: [VanTransferWizard],
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: StockTransfersService, useValue: service },
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
  return { fixture, component, el: fixture.nativeElement as HTMLElement, service };
}

describe('VanTransferWizard', () => {
  it('offers only non-vehicle, non-transit counterparts and defaults to the branch store', () => {
    const { component } = setup('load', ['StockTransfers.Create']);
    expect(component.counterparts().map((l) => l.id)).toEqual(['store', 'wh']);
    expect(component.form.controls.counterpartId.value).toBe('store');
  });

  it('Load: counterpart -> vehicle; runs the whole chain with every permission', async () => {
    const { component, service } = setup('load', ['StockTransfers.Create', 'StockTransfers.Approve', 'StockTransfers.Dispatch', 'StockTransfers.Receive', 'StockTransfers.View']);
    await component.run();
    expect(service.create).toHaveBeenCalledWith(expect.objectContaining({ sourceLocationId: 'store', destinationLocationId: 'van', submit: true }), expect.any(String));
    expect(component.result()!.stoppedAt).toBeNull();
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

  it('retry after a network error resumes the same transfer with the same step keys (no second create, no repeated approve)', async () => {
    const { fixture, component, el, service } = setup('load', ['StockTransfers.Create', 'StockTransfers.Approve', 'StockTransfers.Dispatch', 'StockTransfers.Receive', 'StockTransfers.View']);
    service.dispatch.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 0 })));

    await component.run();
    fixture.detectChanges();
    expect(component.result()).toMatchObject({ stoppedAt: 'dispatch', reason: 'error' });
    expect(el.querySelector('[data-testid="wizard-stopped-error"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="wizard-transfer-link"]')).not.toBeNull();

    await component.retry();
    fixture.detectChanges();
    expect(component.result()!.stoppedAt).toBeNull();
    expect(service.create).toHaveBeenCalledTimes(1);
    expect(service.approve).toHaveBeenCalledTimes(1);
    expect(service.dispatch).toHaveBeenCalledTimes(2);
    expect((service.dispatch.mock.calls[1] as unknown as unknown[])[2]).toBe((service.dispatch.mock.calls[0] as unknown as unknown[])[2]);
    expect(service.receive).toHaveBeenCalledTimes(1);
    expect(el.querySelector('[data-testid="wizard-done"]')).not.toBeNull();
  });

  it('a fresh run after a completed chain uses new keys', async () => {
    const { component, service } = setup('load', ['StockTransfers.Create', 'StockTransfers.Approve', 'StockTransfers.Dispatch', 'StockTransfers.Receive']);
    await component.run();
    await component.run();
    const key = (i: number) => (service.create.mock.calls[i] as unknown as unknown[])[1];
    expect(key(1)).not.toBe(key(0));
  });

  it('stops at Dispatch (not earlier, not later) for a user who can create and approve only, and links the transfer', async () => {
    const { fixture, component, el, service } = setup('unload', ['StockTransfers.Create', 'StockTransfers.Approve', 'StockTransfers.Receive', 'StockTransfers.View']);
    await component.run();
    fixture.detectChanges();
    expect(service.approve).toHaveBeenCalledTimes(1);
    expect(service.dispatch).not.toHaveBeenCalled();
    expect(service.receive).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="wizard-stopped-permission"]')!.textContent).toContain('StockTransfers.Dispatch');
    expect((el.querySelector('[data-testid="wizard-transfer-link"]') as HTMLAnchorElement).getAttribute('href')).toBe('/inventory/stock-transfers/t1');
  });
});
