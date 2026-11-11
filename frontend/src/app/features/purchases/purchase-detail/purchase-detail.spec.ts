import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../shared/scope/scope-testing';
import { PurchasesService } from '../purchases.service';
import type { PurchaseDetail as Detail, PurchaseLine } from '../purchases.models';
import { PurchaseDetail } from './purchase-detail';

const line = {
  id: 'l1', productId: 'pr1', productName: 'Pen', productUnitId: 'pu1', unitLabel: 'Box', unitCode: 'BOX', conversionFactorToBase: 10, unitDecimalPlaces: 0, qty: 5, baseQty: 50,
  baseUnitLabel: 'pc', baseUnitDecimalPlaces: 0, rate: 10, taxRatePercent: 0, otherCharges: 0, discount: 0, taxableValue: 50, taxAmount: 0, lineTotal: 50, unitCost: 1,
  returnedBaseQty: 0, returnableBaseQty: 20, returnUnits: [], internalBatchNo: 'B-1', supplierBatchNumber: null, receivedDate: null,
  locationBalances: [
    { stockLocationId: 'loc-store', code: 'STORE', name: 'Main Store', baseQty: 20 },
    { stockLocationId: 'loc-van', code: 'VAN1', name: 'Van 1', baseQty: 30 },
  ],
} as unknown as PurchaseLine;

const purchase = {
  id: 'p1', purchaseNo: 'PUR-1', supplierId: 's', supplierName: 'Acme', purchaseDate: '2026-11-06', supplierInvoiceNumber: null, notes: null, paymentMethod: 'Cash',
  paidAmount: 0, dueAmount: 50, paymentStatus: 'Due', subtotal: 50, taxTotal: 0, otherChargesTotal: 0, discountTotal: 0, grandTotal: 50, status: 'Completed', createdByName: 'admin',
  createdAt: '2026-11-06T10:00:00Z', cancelledAt: null, currencyId: 'c', currencyCode: 'INR', currencySymbol: '₹', exchangeRateToBase: 1, originalGrandTotal: 50, originalPaidAmount: 0,
  originalDueAmount: 50, baseCurrencyCode: 'INR', baseCurrencySymbol: '₹', currencyDecimalPrecision: 2, baseCurrencyDecimalPrecision: 2, lines: [line], payments: [], returnedAmount: 0,
  originalReturnedAmount: 0, netTotal: 50, originalNetTotal: 50, originalRefundableAmount: 0, returns: [],
  branchId: 'b2', branchCode: 'NTH', branchName: 'North', stockLocationId: 'loc-store', stockLocationCode: 'STORE', stockLocationName: 'Main Store',
} as unknown as Detail;

function setup() {
  const get = vi.fn(() => of(purchase));
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'p1' }), queryParamMap: convertToParamMap({}) } } },
      { provide: PurchasesService, useValue: { get } },
      { provide: AuthService, useValue: { hasPermission: () => false } },
    ],
  });
  const fixture = TestBed.createComponent(PurchaseDetail);
  fixture.detectChanges();
  return { c: fixture.componentInstance, get, el: fixture.nativeElement as HTMLElement };
}

describe('PurchaseDetail scope', () => {
  it('shows branch, location and the returnable quantity per location', () => {
    const { el, get } = setup();

    expect(get).toHaveBeenCalledWith('p1', null);
    expect(el.querySelector('[data-testid="scope-branch"]')?.textContent).toContain('North (NTH)');
    expect(el.querySelector('[data-testid="scope-location"]')?.textContent).toContain('Main Store (STORE)');
    const cell = (el.querySelector('[data-testid="location-balances"]')?.textContent ?? '').replace(/\s+/g, ' ');
    expect(cell).toContain('Main Store (STORE) 20 pc');
    expect(cell).toContain('Van 1 (VAN1) 30 pc');
    expect(cell).toContain('Returnable: 20 pc');
  });

  it('reloads with ?stockLocationId when another return location is chosen, keeping the page rendered', () => {
    const { c, get, el } = setup();

    c.onReturnLocationChange('loc-van');

    expect(get).toHaveBeenLastCalledWith('p1', 'loc-van');
    expect(el.querySelector('[data-testid="scope-branch"]')).not.toBeNull();
    c.onReturnLocationChange('loc-van');
    expect(get).toHaveBeenCalledTimes(2);
  });
});
