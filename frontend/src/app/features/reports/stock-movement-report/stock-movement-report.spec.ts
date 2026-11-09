import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { StockMovementReport } from './stock-movement-report';
import { ReportsService } from '../reports.service';
import type { StockMovementReportRow } from '../reports.models';

const row = (type: StockMovementReportRow['type'], qty: number): StockMovementReportRow => ({
  id: type, occurredAt: '2026-11-06T10:00:00Z', productName: 'Pen', sku: 'PEN', baseUnitLabel: 'pc', type,
  quantityBaseUnits: qty, referenceType: 'SalesInvoice', referenceNo: 'INV-1', userName: 'admin',
  reasonCode: null, enteredQuantity: null, enteredProductUnitId: null, enteredUnitCode: null, conversionFactorSnapshot: null, enteredDecimalPlaces: null, baseDecimalPlaces: 0,
});

describe('StockMovementReport', () => {
  it('offers SaleCancellation as a filter and shows it as a stock-in (good) movement', () => {
    const stockMovement = vi.fn(() => of({ items: [row('SaleCancellation', 5), row('Sale', -5)], total: 2, page: 1, pageSize: 20 }));
    TestBed.configureTestingModule({ providers: [{ provide: ReportsService, useValue: { stockMovement } }] });
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
    TestBed.configureTestingModule({ providers: [{ provide: ReportsService, useValue: { stockMovement } }] });
    const c = TestBed.createComponent(StockMovementReport).componentInstance;
    c.ngOnInit();
    const cell = (i: number, j: number) => c.rows()[i].cells[j] as { text: string; sub?: string };
    expect(cell(0, 2).text).toBe('Adjustment - Damage');
    expect(cell(0, 3).sub).toBe('3 pc');
    expect(cell(1, 3)).toMatchObject({ text: '-20 pc', sub: '2 BOX (x10)' });
    expect(cell(2, 3).sub).toBeUndefined();
    expect(c.filters()[0].options).toContain('PurchaseCancellation');
  });
});
