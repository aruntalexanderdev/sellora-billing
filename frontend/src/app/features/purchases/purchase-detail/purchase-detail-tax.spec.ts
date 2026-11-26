import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../shared/scope/scope-testing';
import { PurchasesService } from '../purchases.service';
import type { PurchaseDetail as Detail, PurchaseLine, PurchaseLineTax } from '../purchases.models';
import { PurchaseDetail } from './purchase-detail';

const tax = (code: string, kind: PurchaseLineTax['kind'], taxAmount: number, extra: Partial<PurchaseLineTax> = {}): PurchaseLineTax => ({
  code, kind, method: 'Percent', ratePercent: 9, amountPerBaseUnit: null, baseAmount: 100, taxAmount, originalBaseAmount: null, originalTaxAmount: null, ...extra,
});
const mkLine = (id: string, name: string, taxes: PurchaseLineTax[] | null, extra: Partial<PurchaseLine> = {}) => ({
  id, productId: id, productName: name, productUnitId: 'u', unitLabel: 'Box', unitCode: 'BOX', conversionFactorToBase: 1, unitDecimalPlaces: 0, qty: 1, baseQty: 1, baseUnitLabel: 'pc',
  baseUnitDecimalPlaces: 0, rate: 100, taxRatePercent: 18, otherCharges: 0, discount: 0, taxableValue: 100, taxAmount: 18, lineTotal: 118, unitCost: 100, returnedBaseQty: 0,
  returnableBaseQty: 1, returnUnits: [], internalBatchNo: null, supplierBatchNumber: null, receivedDate: null, taxes, ...extra,
}) as PurchaseLine;

const purchaseOf = (lines: PurchaseLine[], extra: Partial<Detail> = {}) => ({
  id: 'p1', purchaseNo: 'PUR-1', supplierId: 's', supplierName: 'Acme', purchaseDate: '2026-11-06', supplierInvoiceNumber: null, notes: null, paymentMethod: 'Cash', paidAmount: 0,
  dueAmount: 236, paymentStatus: 'Due', subtotal: 200, taxTotal: 36, otherChargesTotal: 0, discountTotal: 0, grandTotal: 236, status: 'Completed', createdByName: 'admin',
  createdAt: '2026-11-06T10:00:00Z', cancelledAt: null, currencyId: 'c', currencyCode: 'INR', currencySymbol: '₹', exchangeRateToBase: 1, originalGrandTotal: 236, originalPaidAmount: 0,
  originalDueAmount: 236, baseCurrencyCode: 'INR', baseCurrencySymbol: '₹', currencyDecimalPrecision: 2, baseCurrencyDecimalPrecision: 2, lines, payments: [], returnedAmount: 0,
  originalReturnedAmount: 0, netTotal: 236, originalNetTotal: 236, originalRefundableAmount: 0, returns: [], branchId: 'b', taxableTotal: 200, ...extra,
}) as unknown as Detail;

function render(p: Detail) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'p1' }), queryParamMap: convertToParamMap({}) } } },
      { provide: PurchasesService, useValue: { get: () => of(p) } },
      { provide: AuthService, useValue: { hasPermission: () => false } },
    ],
  });
  const fixture = TestBed.createComponent(PurchaseDetail);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const heads = () => Array.from(el.querySelectorAll('[data-testid="tax-col"]')).map((h) => h.textContent!.trim());
  const cells = (r: number) => Array.from(el.querySelectorAll('tbody tr')[r].querySelectorAll('[data-testid="tax-cell"]')).map((c) => c.textContent!.replace(/\s+/g, ' ').trim());
  return { fixture, c: fixture.componentInstance, el, heads, cells };
}

describe('PurchaseDetail tax (TP-3)', () => {
  it('builds one column per distinct component code across lines (CGST/SGST/IGST/Cess)', () => {
    const intra = mkLine('l1', 'Pen', [tax('SGST', 'Sgst', 9), tax('CGST', 'Cgst', 9)], { hsnCode: '4820' });
    const inter = mkLine('l2', 'Ink', [tax('IGST', 'Igst', 18, { ratePercent: 18 }), tax('CESS', 'Cess', 2, { method: 'AmountPerBaseUnit', ratePercent: null, amountPerBaseUnit: 2 })]);
    const { heads, cells, el } = render(purchaseOf([intra, inter]));
    expect(heads()).toEqual(['CGST', 'SGST', 'IGST', 'CESS']);
    expect(cells(0)).toEqual(['₹9.00 (9%)', '₹9.00 (9%)', '—', '—']);
    expect(cells(1)).toEqual(['—', '—', '₹18.00 (18%)', '₹2.00']);
    expect(Array.from(el.querySelectorAll('[data-testid="hsn"]')).map((e) => e.textContent!.trim())).toEqual(['4820', '—']);
  });

  it('shows legacy rows as one "Tax" column and history without rows from the line tax', () => {
    const legacy = mkLine('l1', 'Pen', [tax('GST', 'Other', 18, { isLegacy: true, ratePercent: 18 })]);
    expect(render(purchaseOf([legacy])).heads()).toEqual(['Tax']);
    TestBed.resetTestingModule();
    const bare = render(purchaseOf([mkLine('l1', 'Pen', null)]));
    expect(bare.heads()).toEqual(['Tax']);
    expect(bare.cells(0)).toEqual(['₹18.00 (18%)']);
    expect(bare.el.querySelector('[data-testid="hsn"]')).toBeNull();
  });

  it('shows taxable total, bill discount/charges, place of supply and supplier state', () => {
    const line = mkLine('l1', 'Pen', [tax('CGST', 'Cgst', 9), tax('SGST', 'Sgst', 9)]);
    const { el } = render(purchaseOf([line], {
      ratesIncludeTax: true, billDiscountAmount: 20, billDiscountPercent: 10, billCharges: 5, otherChargesTotal: 5, discountTotal: 20, taxableTotal: 85,
      placeOfSupply: 'IntraState', supplierStateName: 'Karnataka', placeOfSupplyStateName: 'Karnataka',
    }));
    const t = (id: string) => el.querySelector(`[data-testid="${id}"]`)!.textContent!.replace(/\s+/g, ' ').trim();
    expect(t('taxable-total')).toContain('₹85.00');
    expect(t('bill-discount')).toContain('(10%)');
    expect(t('bill-discount')).toContain('₹20.00');
    expect(t('bill-charges')).toContain('₹5.00');
    expect(t('tax-context')).toContain('Same state');
    expect(t('tax-context')).toContain('Supplier state: Karnataka');
    expect(t('tax-context')).toContain('Rates include tax');
    expect(Array.from(el.querySelectorAll('[data-testid="tax-component"]')).map((e) => e.textContent!.replace(/\s+/g, ' ').trim())).toEqual(['CGST₹9.00', 'SGST₹9.00']);
  });

  it('falls back to subtotal + charges - discount when taxableTotal is absent', () => {
    const { c } = render(purchaseOf([mkLine('l1', 'Pen', null)], { taxableTotal: undefined, subtotal: 100, otherChargesTotal: 10, discountTotal: 4 }));
    expect(c.taxable()).toBe(106);
  });

  it('expands a line to its tax rows', () => {
    const { c, fixture, el } = render(purchaseOf([mkLine('l1', 'Pen', [tax('CGST', 'Cgst', 9), tax('SGST', 'Sgst', 9)])]));
    c.toggleLine('l1');
    fixture.detectChanges();
    expect(el.querySelectorAll('.tax-row-detail tbody tr')).toHaveLength(2);
  });
});
