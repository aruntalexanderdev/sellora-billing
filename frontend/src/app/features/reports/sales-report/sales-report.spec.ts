import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../shared/scope/scope-testing';
import { ReportsService } from '../reports.service';
import type { SalesReportRow } from '../reports.models';
import { SalesReport } from './sales-report';

const row = (id: string, extra: Partial<SalesReportRow> = {}): SalesReportRow => ({
  id, invoiceNo: id, invoiceDate: '2026-11-06', customerName: 'Walk-in', subtotal: 1000, taxTotal: 180, discountTotal: 0, grandTotal: 1180, paidAmount: 1180, dueAmount: 0,
  status: 'Completed', branchId: 'b', branchCode: 'B', branchName: 'Main', taxableTotal: 1000, taxByKind: [], ...extra,
});

function setup(items: SalesReportRow[]) {
  TestBed.configureTestingModule({
    providers: [
      ...provideScopeStubs(),
      { provide: ReportsService, useValue: { sales: () => of({ items, total: items.length, page: 1, pageSize: 20 }) } },
      { provide: AuthService, useValue: { hasPermission: () => false } },
    ],
  });
  const c = TestBed.createComponent(SalesReport).componentInstance;
  c.ngOnInit();
  return c;
}
const text = (c: SalesReport, r: number) => c.rows()[r].cells.map((x) => (x as { text: string }).text);

describe('SalesReport tax columns (TP-4)', () => {
  it('adds Taxable and one column per tax kind present, in kind order', () => {
    const c = setup([
      row('S1', { taxByKind: [{ kind: 'Sgst', taxAmount: 90 }, { kind: 'Cgst', taxAmount: 90 }] }),
      row('S2', { taxableTotal: 500, taxTotal: 100, taxByKind: [{ kind: 'Igst', taxAmount: 90 }, { kind: 'Cess', taxAmount: 10 }] }),
    ]);
    expect(c.cols().map((x) => x.label)).toEqual(['Invoice', 'Date', 'Customer', 'Subtotal', 'Discount', 'Taxable', 'Tax', 'CGST', 'SGST', 'IGST', 'Cess', 'Total', 'Paid', 'Due']);
    expect(text(c, 0).slice(3, 11)).toEqual(['₹1,000', '₹0', '₹1,000', '₹180', '₹90', '₹90', '₹0', '₹0']);
    expect(text(c, 1).slice(5, 11)).toEqual(['₹500', '₹100', '₹0', '₹0', '₹90', '₹10']);
    expect(c.rows()[0].cells).toHaveLength(c.cols().length);
  });

  it('shows legacy tax under "Other tax" and derives taxable when the server omits it', () => {
    const c = setup([row('S1', { taxableTotal: undefined, discountTotal: 50, taxByKind: [{ kind: 'Other', taxAmount: 180 }] })]);
    expect(c.cols().map((x) => x.label)).toContain('Other tax');
    expect(text(c, 0)[5]).toBe('₹950');
  });

  it('has no tax-kind column when no row carries tax components', () => {
    const c = setup([row('S1')]);
    expect(c.cols().map((x) => x.label)).toEqual(['Invoice', 'Date', 'Customer', 'Subtotal', 'Discount', 'Taxable', 'Tax', 'Total', 'Paid', 'Due']);
  });
});
