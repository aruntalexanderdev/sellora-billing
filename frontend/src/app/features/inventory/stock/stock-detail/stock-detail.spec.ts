import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
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
      productId: 'p', sku: 'PEN', name: 'Pen', category: null, baseUnitLabel: 'pc', taxLabel: 'GST', quantityOnHand: 17,
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
