import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { PurchaseForm } from './purchase-form';
import { PurchasesService } from '../purchases.service';
import { SuppliersService } from '../suppliers/suppliers.service';
import { ProductsService } from '../../inventory/products/products.service';
import { ExchangeRatesService } from '../../administration/exchange-rates/exchange-rates.service';
import { CashBankService } from '../../accounts/cash-bank/cash-bank.service';
import { AuthService } from '../../../core/auth/auth.service';
import { ContextService } from '../../../core/context/context.service';
import { StockLocationsService } from '../../../shared/locations/stock-locations.service';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { PREVIEW_DEBOUNCE_MS, TaxPreviewService } from '../../../shared/tax/tax-preview.service';
import type { TaxDocumentPreview, TaxDocumentPreviewRequest, TaxPreviewLine } from '../../../shared/tax/tax-preview.models';

const product = {
  id: 'pen', baseUnitLabel: 'pc', taxRatePercent: 0,
  units: [{ id: 'pu', unitCode: 'pc', displayName: 'Piece', decimalPlaces: 0, conversionFactorToBase: 1, isBaseUnit: true, isPurchasable: true, isDefaultPurchaseUnit: true, sortOrder: 0, isActive: true }],
};

const row = (code: string, kind: 'Cgst' | 'Sgst', tax: number) => ({ code, kind, method: 'Percent' as const, ratePercent: 9, amountPerBaseUnit: null, baseAmount: 100, taxAmount: tax });
function previewOf(extra: Partial<TaxDocumentPreview> = {}): TaxDocumentPreview {
  const line = {
    index: 0, productId: 'pen', productName: 'Pen', productUnitId: 'pu', unitCode: 'pc', baseQty: 2, profileName: 'GST 18%', hsnCode: '4820', priceIncludesTax: false,
    gross: 100, taxable: 100, taxTotal: 18, lineTotal: 118, rows: [row('CGST', 'Cgst', 9), row('SGST', 'Sgst', 9)],
  } as unknown as TaxPreviewLine;
  return {
    direction: 'Purchase', placeOfSupply: 'IntraState', regime: 'Gst', precision: 2, subtotal: 100, discountTotal: 0, chargesTotal: 0, taxableTotal: 100, taxTotal: 18, grandTotal: 118,
    billDiscountAmount: 0, effectiveBillDiscountPercent: 0, lines: [line], warnings: [], ...extra,
  } as unknown as TaxDocumentPreview;
}

function setup(opts: { preview?: (r: TaxDocumentPreviewRequest) => unknown; create?: () => unknown } = {}) {
  const preview = vi.fn<(r: TaxDocumentPreviewRequest) => unknown>(opts.preview ?? (() => of(previewOf())));
  const create = vi.fn<(...args: unknown[]) => unknown>(opts.create ?? (() => of({ id: 'po-1', purchaseNo: 'PO-1' })));
  TestBed.configureTestingModule({
    imports: [PurchaseForm],
    providers: [
      provideRouter([]),
      { provide: PurchasesService, useValue: { create } },
      { provide: SuppliersService, useValue: { list: () => of({ items: [{ id: 's1', name: 'Acme', isActive: true, currencyId: 'c', currencyCode: 'INR', currencySymbol: '₹', currencyDecimalPrecision: 2 }] }) } },
      { provide: ProductsService, useValue: { list: () => of({ items: [{ id: 'pen', name: 'Pen', sku: 'P', isActive: true }] }), get: () => of(product) } },
      { provide: ExchangeRatesService, useValue: { resolve: () => of({ rateToBase: 1, baseCurrencyCode: 'INR', baseCurrencySymbol: '₹', baseCurrencyDecimalPrecision: 2 }) } },
      { provide: CashBankService, useValue: { options: () => of([]) } },
      { provide: StockLocationsService, useValue: { list: () => of([]) } },
      { provide: AuthService, useValue: { hasPermission: () => false } },
      { provide: ContextService, useValue: { contextVersion: signal(0).asReadonly(), workingBranch: signal({ id: 'b1' }) } },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: TaxPreviewService, useValue: { preview } },
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(PurchaseForm);
  fixture.detectChanges();
  const c = fixture.componentInstance;
  const fill = (over: Record<string, unknown> = {}) => {
    c.form.patchValue({ supplierId: 's1' });
    c.onProductChange(c.lines.at(0), 'pen');
    c.lines.at(0).patchValue({ productId: 'pen', productUnitId: 'pu', qty: 2, rate: 50, ...over });
    fixture.detectChanges();
  };
  const settle = () => {
    vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
    fixture.detectChanges();
  };
  return { fixture, c, el: fixture.nativeElement as HTMLElement, preview, create, navigate, fill, settle };
}

describe('PurchaseForm tax (TP-3)', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('requests the preview once, debounced, with the purchase request shape', () => {
    const { c, fixture, preview, fill, settle } = setup();
    fill();
    c.form.patchValue({ ratesIncludeTax: true, billCharges: 7 });
    c.lines.at(0).patchValue({ qty: 3 });
    fixture.detectChanges();
    expect(preview).not.toHaveBeenCalled();
    settle();
    expect(preview).toHaveBeenCalledTimes(1);
    expect(preview.mock.calls[0][0]).toEqual({
      direction: 'Purchase', partyId: 's1', date: c.form.controls.purchaseDate.value, ratesIncludeTax: true, billDiscountPercent: null, billDiscountAmount: null, billCharges: 7,
      lines: [{ productId: 'pen', productUnitId: 'pu', qty: 3, unitPrice: 50, discount: 0, otherCharges: 0 }],
    });
  });

  it('ignores a stale response: only the newest request lands', () => {
    const first = new Subject<TaxDocumentPreview>();
    const { c, fixture, fill, settle } = setup({ preview: (r) => (r.lines[0].qty === 2 ? first : of(previewOf({ grandTotal: 236 }))) });
    fill();
    settle();
    c.lines.at(0).patchValue({ qty: 4 });
    fixture.detectChanges();
    settle();
    first.next(previewOf({ grandTotal: 999 }));
    expect(c.totals().grandTotal).toBe(236);
  });

  it('falls back to the local line estimate while a newer breakdown is pending (no stale figures on shifted rows)', () => {
    const { c, fixture, fill, settle } = setup();
    fill();
    settle();
    expect(c.lineViews()[0]).toMatchObject({ tax: 18, estimated: false });
    c.lines.at(0).patchValue({ qty: 5 });
    fixture.detectChanges();
    expect(c.previewPending()).toBe(true);
    expect(c.lineViews()[0]).toMatchObject({ tax: 0, estimated: true, taxable: 250 });
    settle();
    expect(c.lineViews()[0]).toMatchObject({ tax: 18, estimated: false });
  });

  it('shows the server breakdown: line tax, taxable, tax by component, grand total and place of supply', () => {
    const { c, el, fixture, fill, settle } = setup();
    fill();
    settle();
    expect(c.totals()).toMatchObject({ taxable: 100, tax: 18, grandTotal: 118, estimated: false });
    expect(c.totals().byComponent.map((x) => [x.code, x.taxAmount])).toEqual([['CGST', 9], ['SGST', 9]]);
    expect(el.querySelector('[data-testid="place-of-supply"]')?.textContent).toContain('Same state');
    expect(Array.from(el.querySelectorAll('[data-testid="tax-component"]')).map((e) => e.textContent!.replace(/\s+/g, ' ').trim())).toEqual(['CGST₹9.00', 'SGST₹9.00']);

    (el.querySelector('[data-testid="tax-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelectorAll('[data-testid="line-tax-detail"] tbody tr')).toHaveLength(2);
  });

  it('badges Other state and VAT', () => {
    const inter = setup({ preview: () => of(previewOf({ placeOfSupply: 'InterState' })) });
    inter.fill();
    inter.settle();
    expect(inter.c.placeLabel()).toBe('Other state');
    TestBed.resetTestingModule();
    const vat = setup({ preview: () => of(previewOf({ placeOfSupply: 'NotApplicable', regime: 'Vat' })) });
    vat.fill();
    vat.settle();
    expect(vat.c.placeLabel()).toBe('VAT');
  });

  it('keeps a client subtotal fallback (no tax) while loading and when the preview fails', () => {
    const { c, fixture, el, fill, settle } = setup({
      preview: () => throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { 'lines[0].rate': ['Rate has too many decimals.'] } } })),
    });
    fill({ discount: 10 });
    expect(c.totals()).toMatchObject({ subtotal: 100, discount: 10, taxable: 90, tax: 0, grandTotal: 90, estimated: true });
    settle();
    expect(c.totals().estimated).toBe(true);
    expect(c.previewError()).toContain('too many decimals');
    expect(el.querySelector('[data-testid="preview-error"]')).not.toBeNull();
    fixture.detectChanges();
  });

  it('lists preview warnings with the form item number', () => {
    const { c, el, fixture, fill, settle } = setup({
      preview: () => of(previewOf({ warnings: [{ field: 'supplierId', message: 'The supplier has no state.' }, { field: 'lines[0].hsnCode', message: 'No HSN code.' }] })),
    });
    fill();
    settle();
    expect(c.previewWarnings()).toEqual(['The supplier has no state.', 'Item 1: No HSN code.']);
    expect(el.querySelector('[data-testid="preview-warnings"]')?.textContent).toContain('No HSN code.');
    fixture.detectChanges();
  });

  describe('bill discount is a percent or an amount, never both', () => {
    it('clears the other control when the mode changes and sends only the active one', () => {
      const { c, create, fill } = setup();
      fill();
      c.form.patchValue({ billDiscountPercent: 10 });
      c.setBillDiscountMode('amount');
      expect(c.form.controls.billDiscountPercent.value).toBeNull();
      c.form.patchValue({ billDiscountAmount: 25 });
      c.submit();
      let body = create.mock.calls[0][0] as Record<string, unknown>;
      expect(body['billDiscountPercent']).toBeNull();
      expect(body['billDiscountAmount']).toBe(25);

      c.setBillDiscountMode('percent');
      expect(c.form.controls.billDiscountAmount.value).toBeNull();
      c.form.patchValue({ billDiscountPercent: 5, billCharges: 3, ratesIncludeTax: true });
      c.submit();
      body = create.mock.calls[1][0] as Record<string, unknown>;
      expect(body).toMatchObject({ billDiscountPercent: 5, billDiscountAmount: null, billCharges: 3, ratesIncludeTax: true });
    });

    it('renders only the active input', () => {
      const { c, el, fixture } = setup();
      expect(el.querySelector('[data-testid="bill-discount-percent"]')).not.toBeNull();
      expect(el.querySelector('[data-testid="bill-discount-amount"]')).toBeNull();
      c.setBillDiscountMode('amount');
      fixture.detectChanges();
      expect(el.querySelector('[data-testid="bill-discount-percent"]')).toBeNull();
      expect(el.querySelector('[data-testid="bill-discount-amount"]')).not.toBeNull();
    });
  });

  describe('save', () => {
    it('maps lines[i].rate and header errors onto the controls', () => {
      const problem = { title: 'Validation failed', errors: { 'lines[0].rate': ['Rate has more than 4 decimals.'], billDiscount: ['Use a percent or an amount.'], billCharges: ['Too precise.'] } };
      const { c, el, fixture, fill } = setup({ create: () => throwError(() => new HttpErrorResponse({ status: 400, error: problem })) });
      fill();
      c.form.patchValue({ billDiscountPercent: 5 });
      c.submit();
      fixture.detectChanges();
      expect(c.lines.at(0).controls.rate.errors?.['server']).toBe('Rate has more than 4 decimals.');
      expect(el.querySelector('[data-testid="rate-error"]')?.textContent).toContain('more than 4 decimals');
      expect(c.form.controls.billDiscountPercent.errors?.['server']).toBe('Use a percent or an amount.');
      expect(c.form.controls.billCharges.errors?.['server']).toBe('Too precise.');
      expect(c.serverError()).toBeNull();
    });

    it('shows branchId and lines (rate changed during save) errors in the banner', () => {
      const problem = { errors: { branchId: ['Set the branch state first.'], lines: ['A tax rate changed while saving. Try again.'] } };
      const { c, fill } = setup({ create: () => throwError(() => new HttpErrorResponse({ status: 400, error: problem })) });
      fill();
      c.submit();
      expect(c.serverError()).toBe('Set the branch state first. A tax rate changed while saving. Try again.');
    });

    it('hands the create warnings to the detail screen', () => {
      const warnings = [{ field: 'supplierId', message: 'The supplier has no state.' }, { field: 'lines[0].hsnCode', message: 'No HSN code.' }];
      const { c, navigate, fill } = setup({ create: () => of({ id: 'po-1', purchaseNo: 'PO-1', warnings }) });
      fill();
      c.submit();
      expect(c.saveWarnings()).toEqual(['The supplier has no state.', 'Item 1: No HSN code.']);
      expect(navigate).toHaveBeenCalledWith(['/purchases', 'po-1'], { state: { warnings: c.saveWarnings() } });
    });
  });
});
