import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../shared/scope/scope-testing';
import { ReportsService } from '../reports.service';
import type { PurchaseReportRow } from '../reports.models';
import { PurchaseReport, reportTaxKinds } from './purchase-report';

const row = (id: string, extra: Partial<PurchaseReportRow> = {}): PurchaseReportRow => ({
  id, purchaseNo: id, purchaseDate: '2026-11-06', supplierName: 'Acme', subtotal: 1000, taxTotal: 180, otherChargesTotal: 0, grandTotal: 1180, paidAmount: 0, dueAmount: 1180,
  status: 'Completed', currencyCode: 'INR', originalGrandTotal: 1180, branchId: 'b', branchCode: 'B', branchName: 'Main', taxableTotal: 1000, taxByKind: [], ...extra,
});

function setup(items: PurchaseReportRow[]) {
  TestBed.configureTestingModule({
    providers: [
      ...provideScopeStubs(),
      { provide: ReportsService, useValue: { purchases: () => of({ items, total: items.length, page: 1, pageSize: 20 }) } },
      { provide: AuthService, useValue: { hasPermission: () => false } },
    ],
  });
  const c = TestBed.createComponent(PurchaseReport).componentInstance;
  c.ngOnInit();
  return c;
}

describe('PurchaseReport tax columns (TP-3)', () => {
  it('adds Taxable and one column per tax kind present, in kind order', () => {
    const c = setup([
      row('P1', { taxByKind: [{ kind: 'Sgst', taxAmount: 90 }, { kind: 'Cgst', taxAmount: 90 }] }),
      row('P2', { taxableTotal: 500, taxTotal: 100, taxByKind: [{ kind: 'Igst', taxAmount: 90 }, { kind: 'Cess', taxAmount: 10 }] }),
    ]);
    expect(c.cols().map((x) => x.label)).toEqual(['Purchase No', 'Date', 'Supplier', 'Subtotal', 'Taxable', 'Tax', 'CGST', 'SGST', 'IGST', 'Cess', 'Other', 'Total', 'Paid', 'Due']);
    const text = (r: number) => c.rows()[r].cells.map((x) => (x as { text: string }).text);
    expect(text(0).slice(3, 10)).toEqual(['₹1,000', '₹1,000', '₹180', '₹90', '₹90', '₹0', '₹0']);
    expect(text(1).slice(4, 10)).toEqual(['₹500', '₹100', '₹0', '₹0', '₹90', '₹10']);
    expect(c.rows()[0].cells).toHaveLength(c.cols().length);
  });

  it('shows legacy tax under "Other tax" and falls back for rows without taxableTotal', () => {
    const c = setup([row('P1', { taxableTotal: undefined, otherChargesTotal: 20, taxByKind: [{ kind: 'Other', taxAmount: 180 }] })]);
    expect(c.cols().map((x) => x.label)).toContain('Other tax');
    expect((c.rows()[0].cells[4] as { text: string }).text).toBe('₹1,020');
    expect(reportTaxKinds([row('x')])).toEqual([]);
  });
});
