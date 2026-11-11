import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { PurchasesService } from '../../purchases.service';
import type { PurchaseReturnDetail as Detail } from '../../purchases.models';
import { PurchaseReturnDetail } from './purchase-return-detail';

const ret: Detail = {
  id: 'r1', returnNo: 'PR-1', returnDate: '2026-11-06', purchaseOrderId: 'p1', purchaseNo: 'PUR-1', supplierName: 'Acme', reason: null, taxableTotal: 10, taxTotal: 0, total: 10,
  originalTotal: 10, refundedAmount: 0, currencyCode: 'INR', currencySymbol: '₹', currencyDecimalPrecision: 2, baseCurrencyCode: 'INR', baseCurrencySymbol: '₹', baseCurrencyDecimalPrecision: 2,
  createdByName: 'admin', createdAt: '2026-11-06T10:00:00Z', lines: [], refunds: [],
  branchId: 'b2', branchCode: 'NTH', branchName: 'North', stockLocationId: 'l1', stockLocationCode: 'WH', stockLocationName: 'Warehouse',
};

describe('PurchaseReturnDetail scope', () => {
  it('shows the branch and the issuing location', () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'r1' }) } } },
        { provide: PurchasesService, useValue: { getReturn: vi.fn(() => of(ret)) } },
      ],
    });
    const fixture = TestBed.createComponent(PurchaseReturnDetail);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('[data-testid="scope-branch"]')?.textContent).toContain('North (NTH)');
    expect(el.querySelector('[data-testid="scope-location"]')?.textContent).toContain('Warehouse (WH)');
    expect(el.textContent).toContain('Issued from');
  });
});
