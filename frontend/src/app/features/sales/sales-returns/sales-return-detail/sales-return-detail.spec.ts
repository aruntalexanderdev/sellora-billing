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

function render(detail: Detail) {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'r1' }) } } },
      { provide: SalesService, useValue: { getReturn: vi.fn(() => of(detail)) } },
    ],
  });
  const fixture = TestBed.createComponent(SalesReturnDetail);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}
const rr = (code: string, kind: 'Cgst' | 'Sgst' | 'Igst', tax: number) => ({ code, kind, method: 'Percent' as const, ratePercent: 9, amountPerBaseUnit: null, baseAmount: 100, taxAmount: tax });
const retLine = (over: Partial<Detail['lines'][number]> = {}) =>
  ({ id: 'rl', productName: 'Pen', unitLabel: 'Box', unitCode: 'box', qty: 1, baseQty: 10, baseUnitLabel: 'pc', baseUnitDecimalPlaces: 0, taxableValue: 100, taxAmount: 18, lineTotal: 118, ...over }) as Detail['lines'][number];

describe('SalesReturnDetail reversed tax rows (TP-4)', () => {
  it('shows one column per reversed component and the component totals', () => {
    const el = render({ ...ret, taxableTotal: 100, taxTotal: 18, total: 118, lines: [retLine({ taxes: [rr('CGST', 'Cgst', 9), rr('SGST', 'Sgst', 9)] })] });
    expect(Array.from(el.querySelectorAll('[data-testid="tax-col"]')).map((e) => e.textContent!.trim())).toEqual(['CGST', 'SGST']);
    expect(Array.from(el.querySelectorAll('[data-testid="tax-cell"]')).map((e) => e.textContent!.replace(/\s+/g, ' ').trim())).toEqual(['₹9.00 (9%)', '₹9.00 (9%)']);
    expect(Array.from(el.querySelectorAll('[data-testid="tax-component"]')).map((e) => e.textContent!.replace(/\s+/g, ' ').trim())).toEqual(['CGST reversed₹9.00', 'SGST reversed₹9.00']);
  });

  it('shows IGST for an inter-state return and one "Tax" column for a line without rows', () => {
    const inter = render({ ...ret, lines: [retLine({ taxes: [rr('IGST', 'Igst', 18)] })] });
    expect(Array.from(inter.querySelectorAll('[data-testid="tax-col"]')).map((e) => e.textContent!.trim())).toEqual(['IGST']);
    const legacy = render({ ...ret, lines: [retLine({ taxes: [] })] });
    expect(Array.from(legacy.querySelectorAll('[data-testid="tax-col"]')).map((e) => e.textContent!.trim())).toEqual(['Tax']);
    expect(legacy.querySelector('[data-testid="tax-component"]')).toBeNull();
  });
});

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
