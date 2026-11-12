import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { StockDetail } from './stock-detail';
import { StockService } from '../stock.service';
import { AuthService } from '../../../../core/auth/auth.service';
import type { StockDetail as Detail, StockMovement } from '../stock.models';

const mv = (o: Partial<StockMovement>): StockMovement => ({
  id: 'm1', occurredAt: '2026-11-06T10:00:00Z', type: 'Purchase', quantityBaseUnits: 20, referenceType: 'Purchase', referenceNo: 'PO-1',
  userName: 'admin', notes: null, purchaseBatchId: null, internalBatchNo: null, reasonCode: null, enteredQuantity: null,
  enteredProductUnitId: null, enteredUnitCode: null, conversionFactorSnapshot: null, enteredDecimalPlaces: null, ...o,
});

describe('StockDetail movements', () => {
  it('renders type label, tone and entered quantity next to base quantity', () => {
    const detail: Detail = {
      productId: 'p', sku: 'PEN', name: 'Pen', category: null, baseUnitLabel: 'pc', baseDecimalPlaces: 0, taxLabel: 'GST', quantityOnHand: 17,
      averageCost: 1, stockValue: 17, reorderLevel: 0, status: 'Normal', batches: [],
      movements: [
        mv({ id: 'a', type: 'Purchase', enteredQuantity: 2, enteredUnitCode: 'BOX', conversionFactorSnapshot: 10, enteredDecimalPlaces: 0 }),
        mv({ id: 'b', type: 'Adjustment', quantityBaseUnits: -3, reasonCode: 'Damage', baseDecimalPlaces: 2 }),
      ],
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: StockService, useValue: { detail: () => of(detail) } },
        { provide: AuthService, useValue: { hasPermission: () => false } },
      ],
    });
    const f = TestBed.createComponent(StockDetail);
    f.detectChanges();
    const text = (f.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Adjustment - Damage');
    expect(text).toContain('2 BOX (x10)');
    expect(text).toContain('+20 pc');
    expect(text).toContain('-3.00 pc');
  });
});

describe('StockDetail locations', () => {
  const baseDetail: Detail = {
    productId: 'p', sku: 'PEN', name: 'Pen', category: null, baseUnitLabel: 'pc', baseDecimalPlaces: 0, taxLabel: 'GST', quantityOnHand: 30,
    averageCost: 1, stockValue: 30, reorderLevel: 0, status: 'Normal', movements: [], batches: [],
    locations: [
      { locationId: 'l1', code: 'MAIN', name: 'Main Store', type: 'BranchStore', qty: 25, avgCost: 1, value: 25 },
      { locationId: 'l2', code: 'TRN', name: 'In transit', type: 'Transit', qty: 5, avgCost: 1, value: 5 },
    ],
  };

  function mount(detail: Detail) {
    const detailFn = vi.fn((..._a: unknown[]) => of(detail));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'p' }) } } },
        { provide: StockService, useValue: { detail: detailFn } },
        { provide: AuthService, useValue: { hasPermission: () => false } },
      ],
    });
    const f = TestBed.createComponent(StockDetail);
    f.detectChanges();
    return { f, el: f.nativeElement as HTMLElement, detailFn };
  }

  it('renders the per-location table with a Transit badge for Transit locations', () => {
    const { el } = mount(baseDetail);
    const rows = Array.from(el.querySelectorAll('[data-testid="location-row"]'));
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Main Store');
    expect(rows[0].textContent).toContain('25 pc');
    expect(rows[0].textContent).not.toContain('Transit');
    expect(rows[1].textContent).toContain('Transit');
  });

  it('opts the load into primaryLoad (third argument)', () => {
    const { detailFn } = mount(baseDetail);
    expect(detailFn).toHaveBeenCalledWith('p', null, true);
  });

  it('shows the movement location and links StockTransfer references to the transfer', () => {
    const d: Detail = {
      ...baseDetail,
      movements: [
        mv({ id: 'a', type: 'TransferOut', quantityBaseUnits: -5, referenceType: 'StockTransfer', referenceNo: 'TRF-1', referenceId: 'tr-1', stockLocationCode: 'MAIN', stockLocationName: 'Main Store' }),
        mv({ id: 'b', type: 'Purchase', referenceType: 'Purchase', referenceNo: 'PO-9', referenceId: 'po-9', stockLocationCode: 'MAIN' }),
        mv({ id: 'c', type: 'TransferIn', referenceType: 'StockTransfer', referenceNo: 'TRF-2', referenceId: null }),
      ],
    };
    const { el } = mount(d);
    const link = el.querySelector('a[href="/inventory/stock-transfers/tr-1"]');
    expect(link?.textContent?.trim()).toBe('TRF-1');
    expect(el.querySelectorAll('a[href^="/inventory/stock-transfers"]').length).toBe(1);
    expect(el.textContent).toContain('Transfer out');
    expect(el.textContent).toContain('Transfer in');
    expect(el.textContent).toContain('MAIN');
  });

  it('shows per-location batch balances when a batch is expanded', () => {
    const d: Detail = {
      ...baseDetail,
      batches: [{
        id: 'b1', createdAt: '2026-11-01T00:00:00Z', sourceType: 'Purchase', sourceRefNo: 'PO-1', purchasedBaseQty: 30, remainingBaseQty: 30, unitCost: 1,
        internalBatchNo: 'B-1', supplierBatchNumber: null, receivedDate: '2026-11-01', purchaseOrderId: null, purchaseOrderLineId: null,
        locations: [{ locationId: 'l1', code: 'MAIN', name: 'Main Store', qty: 25 }, { locationId: 'l2', code: 'TRN', name: 'In transit', qty: 5 }],
      }],
    };
    const { f, el } = mount(d);
    f.componentInstance.toggleBatch(d.batches[0]);
    f.detectChanges();
    expect(el.querySelector('.batch-detail')?.textContent?.replace(/\s+/g, ' ')).toContain('MAIN 25 pc · TRN 5 pc');
  });
});

describe('StockDetail quantity precision', () => {
  const units = [
    { productUnitId: 'u-pc', unitCode: 'PC', label: 'Piece', factor: 1, isBaseUnit: true },
    { productUnitId: 'u-box', unitCode: 'BOX', label: 'Box', factor: 10, isBaseUnit: false },
    { productUnitId: 'u-fc', unitCode: 'FC', label: 'Full Carton', factor: 100, isBaseUnit: false },
  ];
  const rice: Detail = {
    productId: 'r', sku: 'RICE', name: 'Rice', category: null, baseUnitLabel: 'kg', baseDecimalPlaces: 3, taxLabel: 'GST', quantityOnHand: 12,
    averageCost: 1, stockValue: 12, reorderLevel: 0, status: 'Normal', movements: [], batches: [],
  };

  function mount(detail: Detail) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'p' }) } } },
        { provide: StockService, useValue: { detail: () => of(detail) } },
        { provide: AuthService, useValue: { hasPermission: () => false } },
      ],
    });
    const f = TestBed.createComponent(StockDetail);
    f.detectChanges();
    return { f, el: f.nativeElement as HTMLElement };
  }

  it('onHand_kgProductWithNoMovements_shows3dp', () => {
    const { el } = mount(rice);
    expect(el.querySelector('.summary-value')?.textContent?.trim()).toBe('12.000 kg');
  });

  it('locations_kgProductWithNoMovements_show3dp', () => {
    const { el } = mount({ ...rice, locations: [{ locationId: 'l1', code: 'MAIN', name: 'Main', type: 'BranchStore', qty: 7, avgCost: 1, value: 7 }] });
    expect(el.querySelector('[data-testid="location-row"]')?.textContent).toContain('7.000 kg');
  });

  it('batches_kgProduct_useBaseDecimalPlaces', () => {
    const d: Detail = {
      ...rice,
      batches: [{
        id: 'b1', createdAt: '2026-11-01T00:00:00Z', sourceType: 'Purchase', sourceRefNo: 'PO-1', purchasedBaseQty: 20, remainingBaseQty: 12, unitCost: 1,
        internalBatchNo: 'B-1', supplierBatchNumber: null, receivedDate: '2026-11-01', purchaseOrderId: null, purchaseOrderLineId: null,
      }],
    };
    const { f, el } = mount(d);
    f.componentInstance.toggleBatch(d.batches[0]);
    f.detectChanges();
    const text = el.textContent?.replace(/\s+/g, ' ') ?? '';
    expect(text).toContain('20.000');
    expect(text).toContain('12.000');
    expect(text).toContain('Consumed 8.000 of 20.000 kg');
  });

  it('breakdown_pen122_rendersOnHandAndPerLocation', () => {
    const { el } = mount({
      ...rice, name: 'Pen', baseUnitLabel: 'pc', baseDecimalPlaces: 0, quantityOnHand: 122, breakdownUnits: units,
      locations: [
        { locationId: 'l1', code: 'MAIN', name: 'Main', type: 'BranchStore', qty: 110, avgCost: 1, value: 110 },
        { locationId: 'l2', code: 'VAN', name: 'Van', type: 'BranchStore', qty: 12, avgCost: 1, value: 12 },
      ],
    });
    expect(el.querySelector('[data-testid="onhand-breakdown"]')?.textContent?.trim()).toBe('1 Full Carton, 2 Box, 2 Piece');
    const loc = Array.from(el.querySelectorAll('[data-testid="location-breakdown"]')).map((e) => e.textContent?.trim());
    expect(loc).toEqual(['1 Full Carton, 1 Box', '1 Box, 2 Piece']);
  });

  it('breakdown_baseOnlyOrNoUnits_isNotRendered', () => {
    const { el } = mount({ ...rice, breakdownUnits: [{ productUnitId: 'u', unitCode: 'KG', label: 'kg', factor: 1, isBaseUnit: true }] });
    expect(el.querySelector('[data-testid="onhand-breakdown"]')).toBeNull();
    TestBed.resetTestingModule();
    expect(mount(rice).el.querySelector('[data-testid="onhand-breakdown"]')).toBeNull();
  });
});
