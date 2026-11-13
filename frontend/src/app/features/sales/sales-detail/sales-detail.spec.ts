import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../shared/scope/scope-testing';
import { SalesService } from '../sales.service';
import type { SalesInvoiceDetail as Detail } from '../sales.models';
import { SalesDetail } from './sales-detail';

const invoice = (over: Partial<Detail> = {}): Detail => ({
  id: 's1', invoiceNo: 'INV-1', customerId: 'c1', customerName: 'Walk-in', invoiceDate: '2026-11-06', paymentMethod: 'Cash', paidAmount: 100, dueAmount: 0, paymentStatus: 'Paid',
  subtotal: 100, taxTotal: 0, discountTotal: 0, grandTotal: 100, status: 'Completed', createdByName: 'admin', createdAt: '2026-11-06T10:00:00Z', cancelledAt: null,
  lines: [], payments: [], returnedAmount: 0, netTotal: 100, refundableAmount: 0, returns: [],
  branchId: 'b2', branchCode: 'NTH', branchName: 'North', sellerGstin: '29ABCDE1234F1Z5', stockLocationId: 'l1', stockLocationCode: 'VAN1', stockLocationName: 'Van 1',
  ...over,
});

function setup(response: Observable<Detail>) {
  const get = vi.fn(() => response);
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 's1' }), queryParamMap: convertToParamMap({}) } } },
      { provide: SalesService, useValue: { get } },
      { provide: AuthService, useValue: { hasPermission: () => false } },
    ],
  });
  const fixture = TestBed.createComponent(SalesDetail);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  return { get, el, text: (sel: string) => (el.querySelector(sel)?.textContent ?? '').replace(/\s+/g, ' ').trim() };
}

describe('SalesDetail free items', () => {
  const line = (id: string, unitPrice: number) =>
    ({ id, productId: 'p', productName: 'Pen', productUnitId: 'u', unitLabel: 'Piece', unitCode: 'pc', conversionFactorToBase: 1, unitDecimalPlaces: 0, qty: 1, baseQty: 1,
      baseUnitLabel: 'pc', baseUnitDecimalPlaces: 0, unitPrice, listUnitPrice: 10, priceOverridden: unitPrice !== 10, taxRatePercent: 0, discount: 0, taxableValue: unitPrice,
      taxAmount: 0, lineTotal: unitPrice, costOfGoodsSold: 0, returnedBaseQty: 0, returnableBaseQty: 0, returnUnits: [], batchConsumptions: [] }) as unknown as Detail['lines'][number];

  it('shows a Free badge only on lines with unit price 0', () => {
    const { el } = setup(of(invoice({ lines: [line('a', 0), line('b', 10)] })));
    expect(el.querySelectorAll('[data-testid="free-badge"]').length).toBe(1);
  });
});

describe('SalesDetail scope', () => {
  it('shows seller GSTIN, branch and stock location', () => {
    const { get, text } = setup(of(invoice()));

    expect(get).toHaveBeenCalledWith('s1');
    expect(text('[data-testid="scope-gstin"]')).toBe('29ABCDE1234F1Z5');
    expect(text('[data-testid="scope-branch"]')).toBe('North (NTH)');
    expect(text('[data-testid="scope-location"]')).toBe('Van 1 (VAN1)');
  });

  it('shows a dash for a legacy invoice without GSTIN or location', () => {
    const { text } = setup(of(invoice({ sellerGstin: null, stockLocationId: null, stockLocationCode: null, stockLocationName: null })));

    expect(text('[data-testid="scope-gstin"]')).toBe('—');
    expect(text('[data-testid="scope-location"]')).toBe('—');
  });

  it('shows "not found" for 404 and the mapped problem for other failures', () => {
    expect(setup(throwError(() => new HttpErrorResponse({ status: 404 }))).el.textContent).toContain('Sale not found.');
    TestBed.resetTestingModule();
    const { text } = setup(throwError(() => new HttpErrorResponse({ status: 500, error: { status: 500, title: 'Boom', traceId: 't-9' } })));
    expect(text('[role="alert"]')).toBe('Boom (Ref: t-9)');
  });
});
