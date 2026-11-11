import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { SalesService } from '../../sales.service';
import type { SalesReturnDetail as Detail } from '../../sales.models';
import { SalesReturnDetail } from './sales-return-detail';

const ret: Detail = {
  id: 'r1', returnNo: 'SR-1', returnDate: '2026-11-06', salesInvoiceId: 's1', invoiceNo: 'INV-1', customerName: 'Walk-in', reason: null, taxableTotal: 10, taxTotal: 0, total: 10,
  refundedAmount: 0, createdByName: 'admin', createdAt: '2026-11-06T10:00:00Z', lines: [], refunds: [],
  branchId: 'b2', branchCode: 'NTH', branchName: 'North', stockLocationId: 'l1', stockLocationCode: 'VAN1', stockLocationName: 'Van 1',
};

describe('SalesReturnDetail scope', () => {
  it('shows the branch and the restock location', () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'r1' }) } } },
        { provide: SalesService, useValue: { getReturn: vi.fn(() => of(ret)) } },
      ],
    });
    const fixture = TestBed.createComponent(SalesReturnDetail);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('[data-testid="scope-branch"]')?.textContent).toContain('North (NTH)');
    expect(el.querySelector('[data-testid="scope-location"]')?.textContent).toContain('Van 1 (VAN1)');
    expect(el.textContent).toContain('Restocked to');
  });
});
