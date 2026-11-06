import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { StockMovementReport } from './stock-movement-report';
import { ReportsService } from '../reports.service';
import type { StockMovementReportRow } from '../reports.models';

const row = (type: StockMovementReportRow['type'], qty: number): StockMovementReportRow => ({
  id: type, occurredAt: '2026-11-06T10:00:00Z', productName: 'Pen', sku: 'PEN', baseUnitLabel: 'pc', type,
  quantityBaseUnits: qty, referenceType: 'SalesInvoice', referenceNo: 'INV-1', userName: 'admin',
});

describe('StockMovementReport', () => {
  it('offers SaleCancellation as a filter and shows it as a stock-in (good) movement', () => {
    const stockMovement = vi.fn(() => of({ items: [row('SaleCancellation', 5), row('Sale', -5)], total: 2, page: 1, pageSize: 20 }));
    TestBed.configureTestingModule({ providers: [{ provide: ReportsService, useValue: { stockMovement } }] });
    const c = TestBed.createComponent(StockMovementReport).componentInstance;
    c.ngOnInit();
    expect(c.filters()[0].options).toContain('SaleCancellation');
    const badge = (i: number) => c.rows()[i].cells[2] as { text: string; tone: string };
    expect(badge(0)).toMatchObject({ text: 'SaleCancellation', tone: 'good' });
    expect(badge(1).tone).toBe('info');
  });
});
