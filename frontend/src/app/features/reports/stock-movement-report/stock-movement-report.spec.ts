import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { StockMovementReport } from './stock-movement-report';
import { AuthService } from '../../../core/auth/auth.service';
import { provideRouter, Router } from '@angular/router';
import { provideScopeStubs } from '../../../shared/scope/scope-testing';
import { ReportsService } from '../reports.service';
import type { StockMovementReportRow } from '../reports.models';

const row = (type: StockMovementReportRow['type'], qty: number): StockMovementReportRow => ({
  id: type, occurredAt: '2026-11-06T10:00:00Z', productName: 'Pen', sku: 'PEN', baseUnitLabel: 'pc', type,
  quantityBaseUnits: qty, referenceType: 'SalesInvoice', referenceNo: 'INV-1', userName: 'admin',
  reasonCode: null, enteredQuantity: null, enteredProductUnitId: null, enteredUnitCode: null, conversionFactorSnapshot: null, enteredDecimalPlaces: null, baseDecimalPlaces: 0,
  stockLocationId: 'l1', stockLocationCode: 'STORE', stockLocationName: 'Main Store',
});

describe('StockMovementReport', () => {
  it('offers SaleCancellation as a filter and shows it as a stock-in (good) movement', () => {
    const stockMovement = vi.fn(() => of({ items: [row('SaleCancellation', 5), row('Sale', -5)], total: 2, page: 1, pageSize: 20 }));
    TestBed.configureTestingModule({ providers: [{ provide: ReportsService, useValue: { stockMovement } }, { provide: AuthService, useValue: { hasPermission: () => false } }] });
    const c = TestBed.createComponent(StockMovementReport).componentInstance;
    c.ngOnInit();
    expect(c.filters()[0].options).toContain('SaleCancellation');
    const badge = (i: number) => c.rows()[i].cells[2] as { text: string; tone: string };
    expect(badge(0)).toMatchObject({ text: 'Sale cancellation', tone: 'good' });
    expect(badge(1).tone).toBe('info');
  });

  it('shows entered qty + unit and reason label, base only for legacy rows', () => {
    const adj = { ...row('Adjustment', -3), reasonCode: 'Damage' as const, enteredQuantity: 3, enteredUnitCode: 'pc', conversionFactorSnapshot: 1, enteredDecimalPlaces: 0 };
    const pur = { ...row('PurchaseCancellation', -20), enteredQuantity: 2, enteredUnitCode: 'BOX', conversionFactorSnapshot: 10, enteredDecimalPlaces: 0 };
    const stockMovement = vi.fn(() => of({ items: [adj, pur, row('Sale', -1)], total: 3, page: 1, pageSize: 20 }));
    TestBed.configureTestingModule({ providers: [{ provide: ReportsService, useValue: { stockMovement } }, { provide: AuthService, useValue: { hasPermission: () => false } }] });
    const c = TestBed.createComponent(StockMovementReport).componentInstance;
    c.ngOnInit();
    const cell = (i: number, j: number) => c.rows()[i].cells[j] as { text: string; sub?: string };
    expect(cell(0, 2).text).toBe('Adjustment - Damage');
    expect(cell(0, 3).sub).toBe('3 pc');
    expect(cell(1, 3)).toMatchObject({ text: '-20 pc', sub: '2 BOX (x10)' });
    expect(cell(2, 3).sub).toBeUndefined();
    expect(c.filters()[0].options).toContain('PurchaseCancellation');
  });

  describe('location scope', () => {
    function mount(items: StockMovementReportRow[], canSeeLocations: boolean) {
      const stockMovement = vi.fn(() => of({ items, total: items.length, page: 1, pageSize: 20 }));
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          ...provideScopeStubs(),
          { provide: ReportsService, useValue: { stockMovement } },
          { provide: AuthService, useValue: { hasPermission: (c: string) => canSeeLocations && c === 'StockLocations.View' } },
        ],
      });
      const fixture = TestBed.createComponent(StockMovementReport);
      fixture.detectChanges();
      return { fixture, c: fixture.componentInstance, stockMovement, el: fixture.nativeElement as HTMLElement };
    }

    it('sends stockLocationId and resets to page 1', () => {
      const { c, stockMovement } = mount([row('Sale', -1)], true);
      c.onPageChange(2);

      c.onLocationChange('loc-van');

      expect(stockMovement).toHaveBeenLastCalledWith(expect.objectContaining({ stockLocationId: 'loc-van', page: 1 }));
    });

    it('shows a Location column only with StockLocations.View, between Reference and User', () => {
      const withView = mount([row('Sale', -1)], true);
      expect(withView.c.cols().map((x) => x.label)).toEqual(['Date', 'Item', 'Type', 'Quantity', 'Reference', 'Location', 'User']);
      expect((withView.c.rows()[0].cells[5] as { text: string }).text).toBe('Main Store (STORE)');

      TestBed.resetTestingModule();
      const without = mount([row('Sale', -1)], false);
      expect(without.c.cols().map((x) => x.label)).not.toContain('Location');
      expect(without.c.rows()[0].cells).toHaveLength(6);
    });

    it('links StockTransfer movements to the transfer and labels TransferOut/TransferIn', () => {
      const out = { ...row('TransferOut', -5), referenceType: 'StockTransfer', referenceNo: 'TRF-1', referenceId: 'tr-1' };
      const inn = { ...row('TransferIn', 5), referenceType: 'StockTransfer', referenceNo: 'TRF-1', referenceId: 'tr-1' };
      const { c } = mount([out, inn, row('Sale', -1)], true);
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

      expect(c.rows()[0].cells[2]).toMatchObject({ text: 'Transfer out' });
      expect(c.rows()[1].cells[2]).toMatchObject({ text: 'Transfer in', tone: 'good' });
      const link = c.rows()[0].cells[4] as { kind: string; onClick: () => void };
      expect(link.kind).toBe('link');
      link.onClick();
      expect(navigate).toHaveBeenCalledWith(['/inventory/stock-transfers', 'tr-1']);
      expect(c.rows()[2].cells[4].kind).toBe('text');
    });

    it('does not link a StockTransfer movement that has no referenceId', () => {
      const { c } = mount([{ ...row('TransferIn', 5), referenceType: 'StockTransfer', referenceNo: 'TRF-9' }], true);

      expect(c.rows()[0].cells[4].kind).toBe('text');
    });
  });
});
