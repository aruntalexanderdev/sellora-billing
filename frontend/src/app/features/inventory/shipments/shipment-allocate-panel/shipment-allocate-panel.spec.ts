import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { StockService } from '../../stock/stock.service';
import { ProductUnitsLookup } from '../../stock-transfers/product-units-lookup';
import { StockTransfersService } from '../../stock-transfers/stock-transfers.service';
import { line, transfer } from '../../stock-transfers/test-fixtures';
import { shipment } from '../shipment-fixtures';
import { ShipmentsService } from '../shipments.service';
import { ShipmentAllocatePanel } from './shipment-allocate-panel';

const unit = (id: string, code: string, factor: number, isBaseUnit = false) => ({ id, unitCode: code, displayName: code, decimalPlaces: 0, conversionFactorToBase: factor, isBaseUnit, isActive: true });

function setup(allocate = vi.fn(() => of(shipment('Draft')))) {
  const t = transfer('Approved', [line({ requestedBaseQty: 100, requestedQty: 10, allocatedBaseQty: 30, closedBaseQty: 10 })]);
  TestBed.configureTestingModule({
    imports: [ShipmentAllocatePanel],
    providers: [
      {
        provide: StockTransfersService,
        useValue: {
          readyForShipment: vi.fn(() => of({ items: [{ id: 't1', transferNo: 'TRF-0001', status: 'Approved', sourceLocationId: 'store', sourceLocationName: 'Main', destinationLocationId: 'van', destinationLocationName: 'Van 1', destinationBranchName: null, approvedAt: null, lineCount: 1, remainingLineCount: 1 }], total: 1, page: 1, pageSize: 50 })),
          get: vi.fn(() => of(t)),
        },
      },
      { provide: StockService, useValue: { detail: vi.fn(() => of({ locations: [{ locationId: 'store', qty: 25 }] })) } },
      { provide: ProductUnitsLookup, useValue: { units: () => of([unit('pc', 'pc', 1, true), unit('box', 'box', 10)]) } },
      { provide: ShipmentsService, useValue: { allocate } },
    ],
  });
  const fixture = TestBed.createComponent(ShipmentAllocatePanel);
  fixture.componentRef.setInput('shipment', shipment('Draft'));
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  (el.querySelector('.request-head') as HTMLButtonElement).click();
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, el, allocate };
}

describe('ShipmentAllocatePanel', () => {
  it('queries the queue for the shipment source', () => {
    const { component } = setup();
    expect(component.items()).toHaveLength(1);
  });

  it('shows requested, remaining and available in the request unit, falling back to base when not exact', () => {
    const { el } = setup();
    expect(el.querySelector('[data-testid="lim-requested"]')?.textContent).toContain('10 box (100 pc)');
    expect(el.querySelector('[data-testid="lim-remaining"]')?.textContent).toContain('6 box (60 pc)'); // 100 - 10 closed - 30 allocated
    expect(el.querySelector('[data-testid="lim-available"]')?.textContent?.trim()).toBe('25 pc'); // 2.5 box is not representable
  });

  it('defaults the quantity to what fits, and refuses more than is available', () => {
    const { fixture, component, el, allocate } = setup();
    const req = component.request('t1')!;
    expect(req.lines[0].qty).toBe(2); // min(60, 25) pc floored to whole boxes
    const qty = el.querySelector('[data-testid="alloc-qty-1"]') as HTMLInputElement;
    qty.value = '3';
    qty.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (el.querySelector('[data-testid="alloc-add-1"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(allocate).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="alloc-error-1"]')?.textContent).toContain('available');
  });

  it('allocates in the chosen unit with an idempotency key', () => {
    const { fixture, el, allocate } = setup();
    (el.querySelector('[data-testid="alloc-add-1"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(allocate).toHaveBeenCalledWith('s1', { stockTransferLineId: 'l1', productUnitId: 'box', quantity: 2 }, expect.any(String));
  });
});
