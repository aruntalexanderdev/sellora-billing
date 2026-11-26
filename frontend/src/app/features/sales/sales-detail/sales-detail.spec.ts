import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../shared/scope/scope-testing';
import { SalesService } from '../sales.service';
import { SettingsService } from '../../masters/settings/settings.service';
import type { SalesInvoiceDetail as Detail } from '../sales.models';
import { SalesDetail } from './sales-detail';

const invoice = (over: Partial<Detail> = {}): Detail => ({
  id: 's1', invoiceNo: 'INV-1', customerId: 'c1', customerName: 'Walk-in', invoiceDate: '2026-11-06', paymentMethod: 'Cash', paidAmount: 100, dueAmount: 0, paymentStatus: 'Paid',
  subtotal: 100, taxTotal: 0, discountTotal: 0, grandTotal: 100, status: 'Completed', createdByName: 'admin', createdAt: '2026-11-06T10:00:00Z', cancelledAt: null,
  lines: [], payments: [], returnedAmount: 0, netTotal: 100, refundableAmount: 0, returns: [],
  branchId: 'b2', branchCode: 'NTH', branchName: 'North', sellerGstin: '29ABCDE1234F1Z5', stockLocationId: 'l1', stockLocationCode: 'VAN1', stockLocationName: 'Van 1',
  ...over,
});

function setup(response: Observable<Detail>, opts: { permissions?: string[]; settings?: unknown } = {}) {
  const get = vi.fn(() => response);
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 's1' }), queryParamMap: convertToParamMap({}) } } },
      { provide: SalesService, useValue: { get } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => (opts.permissions ?? []).includes(c) } },
      { provide: SettingsService, useValue: { get: () => of(opts.settings) } },
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

describe('SalesDetail tax invoice (TP-4)', () => {
  const trow = (code: string, kind: 'Cgst' | 'Sgst' | 'Igst' | 'Cess' | 'Vat', tax: number, rate: number) =>
    ({ code, kind, method: 'Percent' as const, ratePercent: rate, amountPerBaseUnit: null, baseAmount: 100, taxAmount: tax });
  const tline = (id: string, over: Record<string, unknown> = {}) =>
    ({ id, productId: 'p', productName: 'Pen', productUnitId: 'u', unitLabel: 'Piece', unitCode: 'pc', conversionFactorToBase: 1, unitDecimalPlaces: 0, qty: 2, baseQty: 2,
      baseUnitLabel: 'pc', baseUnitDecimalPlaces: 0, unitPrice: 50, listUnitPrice: 50, priceOverridden: false, taxRatePercent: 18, discount: 0, taxableValue: 100,
      taxAmount: 18, lineTotal: 118, costOfGoodsSold: 0, returnedBaseQty: 0, returnableBaseQty: 0, returnUnits: [], batchConsumptions: [],
      taxes: [trow('CGST', 'Cgst', 9, 9), trow('SGST', 'Sgst', 9, 9)], hsnCode: '4820', priceIncludesTax: false, ...over }) as unknown as Detail['lines'][number];
  const texts = (el: HTMLElement, sel: string) => Array.from(el.querySelectorAll(sel)).map((e) => e.textContent!.replace(/\s+/g, ' ').trim());
  const taxed = (over: Partial<Detail> = {}) =>
    invoice({ lines: [tline('a')], subtotal: 100, taxableTotal: 100, taxTotal: 18, grandTotal: 118, regime: 'Gst', placeOfSupply: 'IntraState', placeOfSupplyStateName: 'Karnataka', sellerStateName: 'Karnataka', ...over });

  it('prints "Tax Invoice" with the seller (name, address, GSTIN) and the place of supply as a state name', () => {
    const { el, text } = setup(of(taxed()), {
      permissions: ['Masters.View'],
      settings: { businessName: 'Sellora Mart', addressLine: '1 MG Road', city: 'Bengaluru', stateName: 'Karnataka', phone: '080 1234', email: null },
    });
    expect(text('[data-testid="invoice-title"]')).toBe('Tax Invoice');
    expect(text('[data-testid="seller-name"]')).toBe('Sellora Mart');
    expect(text('[data-testid="seller-address"]')).toBe('1 MG Road, Bengaluru, Karnataka');
    expect(text('[data-testid="seller-tax-id"]')).toBe('GSTIN: 29ABCDE1234F1Z5');
    expect(text('[data-testid="place-of-supply"]')).toBe('Place of supply: Karnataka (Same state)');
    expect(el.querySelector('[data-testid="invoice-head"]')?.classList.contains('print-only')).toBe(true);
  });

  it('falls back to the branch name without Masters.View, and labels a VAT registration "TRN" without a place of supply state', () => {
    const { text } = setup(of(taxed({ regime: 'Vat', placeOfSupply: 'NotApplicable', placeOfSupplyStateName: null, sellerGstin: '100234567890003' })));
    expect(text('[data-testid="seller-name"]')).toBe('North');
    expect(text('[data-testid="seller-tax-id"]')).toBe('TRN: 100234567890003');
    expect(text('[data-testid="place-of-supply"]')).toBe('Place of supply: — (VAT)');
    expect(text('[data-testid="scope-gstin"]')).toBe('100234567890003');
  });

  it('prints the server seller block and GST state codes for a cashier without Masters.View (T31b)', () => {
    const { text } = setup(of(taxed({
      sellerName: 'Sellora Retail Pvt Ltd', sellerAddress: '1 MG Road, Bengaluru', sellerTaxIdLabel: 'GSTIN', sellerStateGstCode: '29',
      placeOfSupplyStateName: 'Kerala', placeOfSupplyStateGstCode: '32', placeOfSupply: 'InterState',
    })));
    expect(text('[data-testid="seller-name"]')).toBe('Sellora Retail Pvt Ltd');
    expect(text('[data-testid="seller-address"]')).toBe('1 MG Road, Bengaluru, Karnataka (29)');
    expect(text('[data-testid="seller-tax-id"]')).toBe('GSTIN: 29ABCDE1234F1Z5');
    expect(text('[data-testid="place-of-supply"]')).toBe('Place of supply: Kerala (32) (Other state)');
  });

  it('intra-state: HSN, discount, taxable and CGST/SGST columns with a component footer', () => {
    const { el } = setup(of(taxed({ billDiscountAmount: 4, billDiscountPercent: 4, billCharges: 10, discountTotal: 4 })));
    expect(texts(el, '[data-testid="tax-col"]')).toEqual(['CGST', 'SGST']);
    expect(el.querySelector('[data-testid="hsn-col"]')).not.toBeNull();
    expect(texts(el, '[data-testid="hsn"]')).toEqual(['4820']);
    expect(texts(el, '[data-testid="line-taxable"]')).toEqual(['₹100.00']);
    expect(texts(el, '[data-testid="tax-cell"]')).toEqual(['₹9.00 (9%)', '₹9.00 (9%)']);
    expect(texts(el, '[data-testid="tax-component"]')).toEqual(['CGST₹9.00', 'SGST₹9.00']);
    expect(texts(el, '[data-testid="taxable-total"]')).toEqual(['Taxable value₹100.00']);
    expect(texts(el, '[data-testid="bill-discount"]')).toEqual(['of which bill discount (4%)−₹4.00']);
    expect(texts(el, '[data-testid="bill-charges"]')).toEqual(['Bill charges₹10.00']);
    expect(texts(el, '[data-testid="grand-total"]')).toEqual(['₹118.00']);
  });

  it('inter-state with cess: IGST then Cess columns', () => {
    const line = tline('a', { taxes: [trow('Cess', 'Cess', 1, 1), trow('IGST', 'Igst', 18, 18)] });
    const { el } = setup(of(taxed({ lines: [line], placeOfSupply: 'InterState', placeOfSupplyStateName: 'Kerala' })));
    expect(texts(el, '[data-testid="tax-col"]')).toEqual(['IGST', 'Cess']);
    expect(texts(el, '[data-testid="place-of-supply"]')).toEqual(['Place of supply: Kerala (Other state)']);
  });

  it('VAT: a single VAT column', () => {
    const line = tline('a', { taxes: [trow('VAT', 'Vat', 5, 5)], hsnCode: null });
    const { el } = setup(of(taxed({ lines: [line], regime: 'Vat', placeOfSupply: 'NotApplicable' })));
    expect(texts(el, '[data-testid="tax-col"]')).toEqual(['VAT']);
    expect(el.querySelector('[data-testid="hsn-col"]')).toBeNull();
  });

  it('legacy invoice (no rows, no regime): one "Tax" column from the line rate, no place of supply, taxable falls back', () => {
    const line = tline('a', { taxes: [], hsnCode: null, taxRatePercent: 5, taxAmount: 5, taxableValue: 100, lineTotal: 105 });
    const { el } = setup(of(invoice({ lines: [line], subtotal: 100, discountTotal: 0, taxTotal: 5, grandTotal: 105, taxableTotal: undefined, regime: null, placeOfSupply: null })));
    expect(texts(el, '[data-testid="tax-col"]')).toEqual(['Tax']);
    expect(texts(el, '[data-testid="taxable-total"]')).toEqual(['Taxable value₹100.00']);
    expect(el.querySelector('[data-testid="place-of-supply"]')).toBeNull();
    expect(el.querySelector('[data-testid="tax-component"]')).toBeNull();
  });

  it('notes "incl. tax" on the rate of an inclusive line', () => {
    const { el } = setup(of(taxed({ lines: [tline('a', { priceIncludesTax: true }), tline('b')] })));
    expect(texts(el, '[data-testid="incl-tax"]')).toEqual(['incl. tax']);
  });
});
