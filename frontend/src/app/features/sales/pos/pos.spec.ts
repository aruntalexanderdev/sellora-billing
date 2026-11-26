import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { Pos } from './pos';
import { SalesService } from '../sales.service';
import { CustomersService } from '../customers/customers.service';
import { CashBankService } from '../../accounts/cash-bank/cash-bank.service';
import { AuthService } from '../../../core/auth/auth.service';
import { ContextService } from '../../../core/context/context.service';
import { ConfirmDialogService } from '../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { StockLocationsService } from '../../../shared/locations/stock-locations.service';
import type { StockLocation } from '../../../shared/locations/locations.models';
import type { AccountOption } from '../../accounts/cash-bank/cash-bank.models';
import { signal } from '@angular/core';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import type { SaleCatalogProduct, SaleCatalogUnit } from '../sales.models';
import { PREVIEW_DEBOUNCE_MS, TaxPreviewService } from '../../../shared/tax/tax-preview.service';
import type { TaxDocumentPreview, TaxDocumentPreviewRequest, TaxPreviewLine, TaxRowView } from '../../../shared/tax/tax-preview.models';

const unit = (over: Partial<SaleCatalogUnit>): SaleCatalogUnit => ({
  productUnitId: 'pu-pc', unitId: 'pc', unitCode: 'pc', displayName: 'Piece', conversionFactorToBase: 1, decimalPlaces: 0,
  isBaseUnit: true, isDefault: false, currentPrice: 10, barcode: null, ...over,
});

const PEN: SaleCatalogProduct = {
  productId: 'pen', sku: 'PEN-1', name: 'Pen', category: null, baseUnitLabel: 'pc', quantityOnHand: 500, taxRatePercent: 0,
  units: [
    unit({ barcode: '111' }),
    unit({ productUnitId: 'pu-box', unitId: 'box', unitCode: 'box', displayName: 'Box', conversionFactorToBase: 10, isBaseUnit: false, isDefault: true, currentPrice: 90, barcode: '222' }),
    unit({ productUnitId: 'pu-ctn', unitId: 'ctn', unitCode: 'ctn', displayName: 'Carton', conversionFactorToBase: 100, isBaseUnit: false, currentPrice: null, barcode: '333' }),
  ],
};
const RICE: SaleCatalogProduct = {
  productId: 'rice', sku: 'RICE', name: 'Rice', category: null, baseUnitLabel: 'kg', quantityOnHand: 50, taxRatePercent: 0,
  units: [unit({ productUnitId: 'pu-kg', unitId: 'kg', unitCode: 'kg', displayName: 'Kg', decimalPlaces: 3, isDefault: true, currentPrice: 60 })],
};

const loc = (id: string, type: StockLocation['locationType'], name: string): StockLocation => ({
  id, code: id.toUpperCase(), name, locationType: type, branchId: 'b1', branchCode: 'MAIN', branchName: 'Main', isActive: true,
  canSell: true, canReceivePurchase: true, isSystemManaged: false,
});
const STORE = loc('store', 'BranchStore', 'Main Store');
const VAN = loc('van', 'Vehicle', 'Van 1');

/** A server preview for a request: no tax (or `rate` percent as one GST-like row), exclusive prices, 2 decimals. */
export function previewFor(r: TaxDocumentPreviewRequest, opts: { rate?: number; inclusive?: boolean; billDiscount?: number; place?: 'IntraState' | 'InterState' } = {}): TaxDocumentPreview {
  const rate = opts.rate ?? 0;
  const lines = r.lines.map((l, index) => {
    const gross = Math.round((l.qty * l.unitPrice) * 100) / 100;
    const taxable = Math.round((gross - l.discount) * 100) / 100;
    const tax = opts.inclusive ? Math.round((taxable - taxable / (1 + rate / 100)) * 100) / 100 : Math.round(taxable * rate) / 100;
    const base = opts.inclusive ? taxable - tax : taxable;
    const rows: TaxRowView[] = rate > 0
      ? [{ code: 'CGST', kind: 'Cgst', method: 'Percent', ratePercent: rate / 2, amountPerBaseUnit: null, baseAmount: base, taxAmount: tax / 2 },
         { code: 'SGST', kind: 'Sgst', method: 'Percent', ratePercent: rate / 2, amountPerBaseUnit: null, baseAmount: base, taxAmount: tax / 2 }]
      : [];
    return {
      index, productId: l.productId, productName: 'x', productUnitId: l.productUnitId, unitCode: 'pc', baseQty: l.qty, profileName: `GST ${rate}%`, hsnCode: '4820',
      priceIncludesTax: !!opts.inclusive, gross, lineDiscount: l.discount, taxable: base, taxRatePercent: rate, taxTotal: tax, lineTotal: opts.inclusive ? taxable : base + tax, rows,
    } as unknown as TaxPreviewLine;
  });
  const sum = (f: (l: TaxPreviewLine) => number) => Math.round(lines.reduce((s, l) => s + f(l), 0) * 100) / 100;
  return {
    direction: 'Sale', placeOfSupply: opts.place ?? 'IntraState', regime: 'Gst', precision: 2, subtotal: sum((l) => l.gross), discountTotal: sum((l) => l.lineDiscount) + (opts.billDiscount ?? 0),
    chargesTotal: r.billCharges ?? 0, taxableTotal: sum((l) => l.taxable), taxTotal: sum((l) => l.taxTotal), grandTotal: sum((l) => l.lineTotal) + (r.billCharges ?? 0),
    billDiscountAmount: opts.billDiscount ?? 0, effectiveBillDiscountPercent: 0, lines, warnings: [],
  } as unknown as TaxDocumentPreview;
}

let settle: () => void = () => undefined;
beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

function setup(opts: {
  permissions?: string[]; lookup?: (code: string) => unknown; create?: () => unknown;
  locations?: StockLocation[]; accounts?: AccountOption[]; confirm?: boolean; query?: Record<string, string>;
  preview?: (r: TaxDocumentPreviewRequest) => unknown; rate?: number;
} = {}) {
  const permissions = opts.permissions ?? [];
  const toast = { show: vi.fn() };
  const lookupBarcode = vi.fn(opts.lookup ?? (() => throwError(() => new HttpErrorResponse({ status: 404 }))));
  const create = vi.fn<(...args: unknown[]) => unknown>(opts.create ?? (() => of({ id: 'inv-1', invoiceNo: 'INV-1' })));
  const catalog = vi.fn((..._args: unknown[]) => of([PEN, RICE]));
  const ask = vi.fn(() => Promise.resolve(opts.confirm ?? true));
  const preview = vi.fn<(r: TaxDocumentPreviewRequest) => unknown>(opts.preview ?? ((r) => of(previewFor(r, { rate: opts.rate }))));
  TestBed.configureTestingModule({
    imports: [Pos],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(opts.query ?? {}) } } },
      { provide: SalesService, useValue: { catalog, lookupBarcode, create } },
      { provide: StockLocationsService, useValue: { list: () => of(opts.locations ?? []) } },
      { provide: ContextService, useValue: { contextVersion: signal(0).asReadonly(), workingBranch: signal({ id: 'b1' }) } },
      { provide: ConfirmDialogService, useValue: { ask } },
      { provide: CustomersService, useValue: { list: () => of({ items: [{ id: 'c1', name: 'Walk-in', isActive: true, isWalkIn: true }] }) } },
      { provide: CashBankService, useValue: { options: () => of(opts.accounts ?? []) } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
      { provide: ToastService, useValue: toast },
      { provide: TaxPreviewService, useValue: { preview } },
    ],
  });
  vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(Pos);
  fixture.detectChanges();
  const component = fixture.componentInstance;
  const el: HTMLElement = fixture.nativeElement;
  const scan = (code: string) => {
    component.productQuery.set(code);
    component.scan();
    fixture.detectChanges();
  };
  settle = () => {
    fixture.detectChanges();
    vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
    fixture.detectChanges();
  };
  return { fixture, component, el, toast, lookupBarcode, create, scan, catalog, ask, preview, settle };
}

const sentLine = (create: { mock: { calls: unknown[][] } }) =>
  (create.mock.calls[0][0] as { lines: Record<string, unknown>[] }).lines[0];

const hit = (product: SaleCatalogProduct, productUnitId: string) => () => of({ product, productUnitId });

describe('POS barcode scanning', () => {
  it('adds a line in the scanned unit with its list price', () => {
    const { component, scan } = setup({ lookup: hit(PEN, 'pu-box') });
    scan('222');
    expect(component.lines.length).toBe(1);
    const row = component.lines.at(0).getRawValue();
    expect(row).toMatchObject({ productId: 'pen', productUnitId: 'pu-box', qty: 1, unitPrice: 90 });
    expect(component.productQuery()).toBe('');
  });

  it('increments the quantity on a repeat scan of the same unit and adds a line for another unit', () => {
    let target = 'pu-box';
    const { component, scan } = setup({ lookup: () => of({ product: PEN, productUnitId: target }) });
    scan('222');
    scan('222');
    expect(component.lines.length).toBe(1);
    expect(component.lines.at(0).controls.qty.value).toBe(2);
    target = 'pu-pc';
    scan('111');
    expect(component.lines.length).toBe(2);
  });

  it('refuses an unpriced unit with a toast and adds nothing', () => {
    const { component, toast, scan } = setup({ lookup: hit(PEN, 'pu-ctn') });
    scan('333');
    expect(component.lines.length).toBe(0);
    expect(toast.show).toHaveBeenCalledWith('Pen Carton has no price — set it in Pricing.');
  });

  it('toasts "not found" on a 404', () => {
    const { component, toast, scan } = setup();
    scan('2012345678905');
    expect(component.lines.length).toBe(0);
    expect(toast.show).toHaveBeenCalledWith('No sellable item found for barcode 2012345678905.');
  });
});

describe('POS pricing and units', () => {
  it('makes price and discount read-only without Sales.PriceOverride and sends overridePrice false', () => {
    const { component, fixture, el, create } = setup({ lookup: hit(PEN, 'pu-box') });
    component.addToCart(PEN);
    fixture.detectChanges();
    expect(el.querySelector<HTMLInputElement>('[data-testid="line-price"]')!.readOnly).toBe(true);
    expect(el.querySelector<HTMLInputElement>('[data-testid="line-discount"]')!.readOnly).toBe(true);
    settle(); component.submit();
    expect(sentLine(create)).toMatchObject({ productUnitId: 'pu-box', unitPrice: 90, discount: 0, overridePrice: false });
  });

  it('blocks completion when a price or discount was changed without the permission', () => {
    const { component } = setup();
    component.addToCart(PEN);
    component.lines.at(0).controls.discount.setValue(5);
    expect(component.completeSaleDisabledReason()).toContain('Fix the highlighted items');
  });

  it('sends overridePrice true with the permission when the price differs, and shows the list price', () => {
    const { component, fixture, el, create } = setup({ permissions: ['Sales.PriceOverride'] });
    component.addToCart(PEN);
    component.lines.at(0).controls.unitPrice.setValue(80);
    fixture.detectChanges();
    expect(el.querySelector<HTMLInputElement>('[data-testid="line-price"]')!.readOnly).toBe(false);
    expect(el.querySelector('[data-testid="line-list-price"]')?.textContent).toContain('90.00');
    settle(); component.submit();
    expect(sentLine(create)).toMatchObject({ unitPrice: 80, overridePrice: true });
  });

  it('resets the price and the override when the unit changes', () => {
    const { component } = setup({ permissions: ['Sales.PriceOverride'] });
    component.addToCart(PEN);
    const row = component.lines.at(0);
    row.controls.unitPrice.setValue(80);
    row.controls.productUnitId.setValue('pu-pc');
    component.onUnitChange(row);
    expect(row.controls.unitPrice.value).toBe(10);
    expect(component.isOverride(row)).toBe(false);
  });

  it('marks a manually selected unpriced unit as a blocking line error', () => {
    const { component } = setup();
    component.addToCart(PEN);
    const row = component.lines.at(0);
    row.controls.productUnitId.setValue('pu-ctn');
    component.onUnitChange(row);
    expect(component.lineErrors()[0]).toContain('has no price');
    expect(component.completeSaleDisabledReason()).toBeTruthy();
  });

  it('derives the quantity step and validators from the selected unit', () => {
    const { component } = setup();
    component.addToCart(RICE);
    component.addToCart(PEN);
    const [kg, pen] = [component.lines.at(0), component.lines.at(1)];
    expect(component.stepFor(kg)).toBe('0.001');
    expect(component.stepFor(pen)).toBe('1');
    kg.controls.qty.setValue(1.25);
    expect(kg.controls.qty.valid).toBe(true);
    pen.controls.qty.setValue(1.5);
    expect(pen.controls.qty.errors).toHaveProperty('maxDecimalPlaces');
    // Switching the Pen line to a unit with decimals is validated per unit too.
    kg.controls.productUnitId.setValue('pu-kg');
    kg.controls.qty.setValue(0.0005);
    expect(kg.controls.qty.invalid).toBe(true);
  });
});

describe('POS location', () => {
  const view = ['StockLocations.View'];

  it('shows the picker only with StockLocations.View and more than one eligible location (O5)', () => {
    const withView = setup({ permissions: view, locations: [STORE, VAN] });
    expect(withView.el.querySelector('[data-testid="pos-location"] select')).not.toBeNull();
    TestBed.resetTestingModule();
    const single = setup({ permissions: view, locations: [STORE] });
    expect(single.el.querySelector('[data-testid="pos-location"] select')).toBeNull();
    TestBed.resetTestingModule();
    const noView = setup({ locations: [STORE, VAN] });
    expect(noView.el.querySelector('[data-testid="pos-location"] select')).toBeNull();
  });

  it('without a picker the catalog, barcode and sale omit stockLocationId (server default)', () => {
    const { component, catalog, lookupBarcode, create, scan } = setup({ lookup: hit(PEN, 'pu-box') });
    expect(catalog).toHaveBeenLastCalledWith(undefined, null);
    scan('222');
    expect(lookupBarcode).toHaveBeenCalledWith('222', null);
    settle(); component.submit();
    expect((create.mock.calls[0][0] as { stockLocationId: unknown }).stockLocationId).toBeNull();
  });

  it('defaults to the store, lists vehicles, and passes the location to catalog, barcode and the sale POST', async () => {
    const { component, catalog, lookupBarcode, create, scan, el } = setup({ permissions: view, locations: [STORE, VAN], lookup: hit(PEN, 'pu-box') });
    await Promise.resolve();
    expect(Array.from(el.querySelectorAll('[data-testid="pos-location"] option')).map((o) => o.textContent?.trim())).toEqual(['Main Store (STORE)', 'Van 1 (VAN)']);
    expect(component.locationId()).toBe('store');
    expect(catalog).toHaveBeenLastCalledWith(undefined, 'store');
    await component.onLocationChange('van');
    expect(catalog).toHaveBeenLastCalledWith(undefined, 'van');
    scan('222');
    expect(lookupBarcode).toHaveBeenLastCalledWith('222', 'van');
    settle(); component.submit();
    expect((create.mock.calls[0][0] as { stockLocationId: string }).stockLocationId).toBe('van');
  });

  it('preselects ?locationId when it is an eligible sale location and loads the catalog there', async () => {
    const { component, catalog, el } = setup({ permissions: view, locations: [STORE, VAN], query: { locationId: 'van' } });
    await Promise.resolve();
    expect(component.locationId()).toBe('van');
    expect(catalog).toHaveBeenCalledTimes(1);
    expect(catalog).toHaveBeenLastCalledWith(undefined, 'van');
    expect((el.querySelector('[data-testid="pos-location"] select') as HTMLSelectElement).value).toBe('van');
  });

  it('silently ignores ?locationId that is unknown, inactive, non-selling, or when the user cannot list locations', async () => {
    // [locations, permissions, requested id, catalog location]; with a single eligible location the picker (and default) is absent.
    const cases: [StockLocation[], string[], string, string | null][] = [
      [[STORE, VAN], view, 'missing', 'store'],
      [[STORE, { ...VAN, isActive: false }], view, 'van', null],
      [[STORE, { ...VAN, canSell: false }], view, 'van', null],
      [[STORE, VAN], [], 'van', null],
    ];
    for (const [locations, permissions, locationId, expected] of cases) {
      TestBed.resetTestingModule();
      const { component, catalog } = setup({ permissions, locations, query: { locationId } });
      await Promise.resolve();
      expect(component.locationId()).not.toBe('van');
      expect(catalog).toHaveBeenLastCalledWith(undefined, expected);
    }
  });

  it('asks before changing location with items in the cart, then clears the cart', async () => {
    const { component, ask, catalog } = setup({ permissions: view, locations: [STORE, VAN] });
    component.addToCart(PEN);
    await component.onLocationChange('van');
    expect(ask).toHaveBeenCalledOnce();
    expect(component.lines.length).toBe(0);
    expect(component.locationId()).toBe('van');
    expect(catalog).toHaveBeenLastCalledWith(undefined, 'van');
  });

  it('clears the catalog while the new location catalog loads, so nothing is added from the stale list', async () => {
    const { component, catalog } = setup({ permissions: view, locations: [STORE, VAN] });
    expect(component.catalog().length).toBeGreaterThan(0);
    const pending = new Subject<unknown[]>();
    catalog.mockReturnValueOnce(pending as never);
    await component.onLocationChange('van');
    expect(component.catalog()).toEqual([]);
    pending.next([RICE]);
    expect(component.catalog()).toEqual([RICE]);
  });

  it('keeps the cart and the old location when the change is declined', async () => {
    const { component, catalog } = setup({ permissions: view, locations: [STORE, VAN], confirm: false });
    component.addToCart(PEN);
    const calls = catalog.mock.calls.length;
    await component.onLocationChange('van');
    expect(component.lines.length).toBe(1);
    expect(component.locationId()).toBe('store');
    expect(catalog.mock.calls.length).toBe(calls);
  });

  it('does not ask when the cart is empty', async () => {
    const { component, ask } = setup({ permissions: view, locations: [STORE, VAN] });
    await component.onLocationChange('van');
    expect(ask).not.toHaveBeenCalled();
  });

  it('maps a stockLocationId field error next to the picker', () => {
    const problem = new HttpErrorResponse({ status: 400, error: { title: 'Validation', errors: { StockLocationId: ['This location cannot sell.'] } } });
    const { component, fixture, el } = setup({ permissions: view, locations: [STORE, VAN], create: () => throwError(() => problem) });
    component.addToCart(PEN);
    settle(); component.submit();
    fixture.detectChanges();
    expect(component.locationError()).toBe('This location cannot sell.');
    expect(el.querySelector('[data-testid="pos-location"] .field-error')?.textContent).toContain('This location cannot sell.');
    expect(component.serverError()).toBe('This location cannot sell.');
  });

  it('offers company-level accounts and the working branch accounts only', () => {
    const accounts: AccountOption[] = [
      { id: 'a-co', name: 'Company cash', kind: 'Cash', branchId: null },
      { id: 'a-b1', name: 'Main till', kind: 'Cash', branchId: 'b1' },
      { id: 'a-b2', name: 'North till', kind: 'Cash', branchId: 'b2' },
    ];
    const { component } = setup({ accounts });
    expect(component.accountOptions().map((a) => a.id)).toEqual(['a-co', 'a-b1']);
  });
});

describe('POS money rules (FI-D24)', () => {
  const OVERRIDE = { permissions: ['Sales.PriceOverride'] };

  it('blocks completion and shows an inline error when discount exceeds qty x price', () => {
    const { component, fixture, el, create } = setup(OVERRIDE);
    component.addToCart(PEN); // default unit Box at 90
    const row = component.lines.at(0);
    row.controls.discount.setValue(90.01); // gross is 90 (1 x 90)
    fixture.detectChanges();
    expect(row.errors?.['discountExceedsGross']).toBe(true);
    expect(component.lineErrors()[0]).toMatch(/Discount cannot be more/);
    expect(component.completeSaleDisabledReason()).toBeTruthy();
    expect(el.textContent).toContain('Discount cannot be more than quantity');
    settle(); component.submit();
    expect(create).not.toHaveBeenCalled();
    row.controls.discount.setValue(90);
    expect(component.lineErrors()[0]).toBeNull();
  });

  it('converts a % entry to a rounded amount, capped at the gross, and sends the amount only', () => {
    const { component, create } = setup(OVERRIDE);
    component.addToCart(RICE); // 60 / kg
    const row = component.lines.at(0);
    row.controls.qty.setValue(1.5); // gross 90
    row.controls.discountPercent.setValue(12.345);
    component.onPercentInput(row);
    expect(row.controls.discount.value).toBe(11.11); // 11.1105
    row.controls.discountPercent.setValue(100);
    component.onPercentInput(row);
    expect(row.controls.discount.value).toBe(90);
    // qty change keeps the amount in step with the %
    row.controls.qty.setValue(2);
    component.onGrossInput(row);
    expect(row.controls.discount.value).toBe(120);
    settle(); component.submit();
    const sent = sentLine(create);
    expect(sent['discount']).toBe(120);
    expect(sent).not.toHaveProperty('discountPercent');
  });

  it('rejects a % below 0 or above 100 with an error and blocks submit', () => {
    const { component, create } = setup(OVERRIDE);
    component.addToCart(PEN);
    const row = component.lines.at(0);
    for (const pct of [-5, 101]) {
      row.controls.discountPercent.setValue(pct);
      component.onPercentInput(row);
      expect(row.controls.discountPercent.errors).toBeTruthy();
      expect(component.lineErrors()[0]).toMatch(/between 0 and 100/);
      expect(row.controls.discount.value).toBe(0);
    }
    settle(); component.submit();
    expect(create).not.toHaveBeenCalled();
  });

  it('typing an amount clears the % entry', () => {
    const { component } = setup(OVERRIDE);
    component.addToCart(PEN);
    const row = component.lines.at(0);
    row.controls.discountPercent.setValue(10);
    component.onDiscountAmountInput(row);
    expect(row.controls.discountPercent.value).toBeNull();
  });

  it('shows a Free badge on a price-0 line only', () => {
    const { component, fixture, el } = setup(OVERRIDE);
    component.addToCart(PEN);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="free-badge"]')).toBeNull();
    component.lines.at(0).controls.unitPrice.setValue(0);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="free-badge"]')?.textContent).toContain('Free');
  });
});

describe('POS tax preview (TP-4)', () => {
  const problem = (errors: Record<string, string[]>, status = 400) => new HttpErrorResponse({ status, error: { title: 'Validation', errors } });

  it('requests one debounced Sale preview with the cart, the customer and the bill charges (no other charges)', () => {
    const { component, fixture, preview } = setup();
    component.addToCart(PEN);
    component.lines.at(0).controls.qty.setValue(3);
    component.form.controls.billCharges.setValue(7);
    fixture.detectChanges();
    expect(preview).not.toHaveBeenCalled();
    settle();
    expect(preview).toHaveBeenCalledTimes(1);
    expect(preview.mock.calls[0][0]).toEqual({
      direction: 'Sale', partyId: 'c1', date: component.form.controls.invoiceDate.value, billDiscountPercent: null, billDiscountAmount: null, billCharges: 7,
      lines: [{ productId: 'pen', productUnitId: 'pu-box', qty: 3, unitPrice: 90, discount: 0 }],
    });
  });

  it('does not preview an empty cart', () => {
    const { preview } = setup();
    settle();
    expect(preview).not.toHaveBeenCalled();
  });

  it('ignores a stale response: only the newest request lands', () => {
    const first = new Subject<TaxDocumentPreview>();
    const { component, fixture } = setup({ preview: (r) => (r.lines[0].qty === 1 ? first : of(previewFor(r))) });
    component.addToCart(PEN);
    settle();
    component.lines.at(0).controls.qty.setValue(4);
    settle();
    first.next(previewFor({ direction: 'Sale', date: 'x', lines: [{ productId: 'pen', productUnitId: 'pu-box', qty: 99, unitPrice: 90, discount: 0 }] }));
    fixture.detectChanges();
    expect(component.totals().grandTotal).toBe(360);
  });

  it('shows a tax-less estimate per line while a newer preview is pending, then the server figures', () => {
    const { component, fixture } = setup({ rate: 18 });
    component.addToCart(PEN);
    settle();
    expect(component.lineViews()[0]).toMatchObject({ estimated: false, tax: 16.2, lineTotal: 106.2 });
    component.lines.at(0).controls.qty.setValue(2);
    fixture.detectChanges();
    expect(component.previewPending()).toBe(true);
    expect(component.lineViews()[0]).toMatchObject({ estimated: true, tax: 0, lineTotal: 180 });
    settle();
    expect(component.lineViews()[0]).toMatchObject({ estimated: false, tax: 32.4, lineTotal: 212.4 });
  });

  it('shows the tax figures, components and place of supply from the preview', () => {
    const { component, fixture, el } = setup({ rate: 18 });
    component.addToCart(PEN);
    settle();
    expect(component.totals()).toMatchObject({ taxable: 90, tax: 16.2, grandTotal: 106.2, estimated: false });
    expect(Array.from(el.querySelectorAll('[data-testid="tax-component"]')).map((e) => e.textContent!.replace(/\s+/g, ' ').trim())).toEqual(['CGST₹8.10', 'SGST₹8.10']);
    expect(el.querySelector('[data-testid="place-of-supply"]')?.textContent).toContain('Same state');
    (el.querySelector('[data-testid="tax-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelectorAll('[data-testid="line-tax-detail"] tbody tr')).toHaveLength(2);
  });

  it('blocks completion while the preview is pending and sends nothing', () => {
    const { component, fixture, create } = setup();
    component.addToCart(PEN);
    fixture.detectChanges();
    expect(component.completeSaleDisabledReason()).toBe('Calculating tax…');
    component.submit();
    expect(create).not.toHaveBeenCalled();
    expect(component.serverError()).toBe('Calculating tax…');
    settle();
    expect(component.completeSaleDisabledReason()).toBeNull();
  });

  it('blocks completion while the preview errors and shows why (stock shortage)', () => {
    const { component, fixture, el, create } = setup({ preview: () => throwError(() => problem({ lines: ['Only 3 pc of Pen in stock.'] })) });
    component.addToCart(PEN);
    settle();
    expect(component.previewError()).toBe('Only 3 pc of Pen in stock.');
    expect(component.completeSaleDisabledReason()).toContain('Only 3 pc of Pen in stock.');
    expect(el.querySelector('[data-testid="preview-error"]')?.textContent).toContain('Only 3 pc');
    expect(component.totals().estimated).toBe(true);
    component.submit();
    expect(create).not.toHaveBeenCalled();
    fixture.detectChanges();
  });

  it('sends expectedGrandTotal = the preview grand total, and the bill charges', () => {
    const { component, create } = setup({ rate: 18 });
    component.addToCart(PEN);
    component.form.controls.billCharges.setValue(10);
    settle();
    component.submit();
    expect(create.mock.calls[0][0]).toMatchObject({ expectedGrandTotal: 116.2, billCharges: 10, billDiscountPercent: null, billDiscountAmount: null });
  });

  it('on a 400 expectedGrandTotal it re-previews, tells the cashier the new total and does not resend', () => {
    let calls = 0;
    const { component, create, preview } = setup({
      create: () => (calls++ === 0 ? throwError(() => problem({ expectedGrandTotal: ['The total is now 110.00.'] })) : of({ id: 'inv-1', invoiceNo: 'INV-1' })),
      rate: 18,
    });
    component.addToCart(PEN);
    settle();
    component.submit();
    expect(create).toHaveBeenCalledTimes(1);
    expect(component.serverError()).toContain('The total is now 110.00.');
    expect(component.saving()).toBe(false);
    expect(component.completeSaleDisabledReason()).toBe('Calculating tax…');
    settle();
    expect(preview).toHaveBeenCalledTimes(2);
    expect(component.completeSaleDisabledReason()).toBeNull();
    expect(create).toHaveBeenCalledTimes(1);
  });

  it('computes change and due from the preview total', () => {
    const { component, fixture, el } = setup({ rate: 18 });
    component.addToCart(PEN);
    settle();
    component.form.controls.paidAmount.setValue(200);
    fixture.detectChanges();
    expect(component.changeAmount()).toBe(93.8);
    expect(el.querySelector('[data-testid="change"]')?.textContent).toContain('₹93.80');
    component.form.controls.paidAmount.setValue(100);
    fixture.detectChanges();
    expect(component.dueAmount()).toBe(6.2);
    expect(el.querySelector('[data-testid="due"]')?.textContent).toContain('₹6.20');
  });

  it('badges inclusive prices per line, from the catalog and from the server', () => {
    const inclusive: SaleCatalogProduct = { ...PEN, priceIncludesTax: true };
    const { component, fixture, el } = setup({ rate: 18 });
    component.catalog.set([inclusive]);
    component.addToCart(inclusive);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="incl-tax"]')?.textContent).toContain('incl. tax');
    expect(el.textContent).toContain('Subtotal (incl. tax)');
  });

  it('shows no inclusive badge on an exclusive line', () => {
    const { component, fixture, el } = setup();
    component.addToCart(PEN);
    settle();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="incl-tax"]')).toBeNull();
  });
});

describe('POS bill discount (TP-4)', () => {
  const BILL = { permissions: ['Sales.BillDiscount'] };
  const problem = (errors: Record<string, string[]>, status = 400) => new HttpErrorResponse({ status, error: { title: 'Validation', errors } });

  it('hides the bill discount without Sales.BillDiscount, never sends it, but still shows bill charges', () => {
    const { component, fixture, el, preview, create } = setup();
    component.addToCart(PEN);
    component.form.patchValue({ billDiscountPercent: 10 });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="bill-discount"]')).toBeNull();
    expect(el.querySelector('[data-testid="bill-charges"]')).not.toBeNull();
    settle();
    expect(preview.mock.calls[0][0]).toMatchObject({ billDiscountPercent: null, billDiscountAmount: null });
    component.submit();
    expect(create.mock.calls[0][0]).toMatchObject({ billDiscountPercent: null, billDiscountAmount: null });
  });

  it('with the permission sends the percent in the preview and the save', () => {
    const { component, fixture, el, preview, create } = setup(BILL);
    component.addToCart(PEN);
    component.form.patchValue({ billDiscountPercent: 10 });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="bill-discount-percent"]')).not.toBeNull();
    settle();
    expect(preview.mock.calls[0][0]).toMatchObject({ billDiscountPercent: 10, billDiscountAmount: null });
    component.submit();
    expect(create.mock.calls[0][0]).toMatchObject({ billDiscountPercent: 10, billDiscountAmount: null });
  });

  it('percent and amount are exclusive: switching the mode clears the other and only one is sent', () => {
    const { component, fixture, el, create } = setup(BILL);
    component.addToCart(PEN);
    component.form.patchValue({ billDiscountPercent: 10 });
    component.setBillDiscountMode('amount');
    fixture.detectChanges();
    expect(component.form.controls.billDiscountPercent.value).toBeNull();
    expect(el.querySelector('[data-testid="bill-discount-percent"]')).toBeNull();
    expect(el.querySelector('[data-testid="bill-discount-amount"]')).not.toBeNull();
    component.form.patchValue({ billDiscountAmount: 5 });
    settle();
    component.submit();
    expect(create.mock.calls[0][0]).toMatchObject({ billDiscountPercent: null, billDiscountAmount: 5 });
  });

  it('shows the preview cap error on the bill discount and blocks completion', () => {
    const { component, fixture, el } = setup({ ...BILL, preview: () => throwError(() => problem({ billDiscountPercent: ['Bill discount cannot exceed 5%.'] })) });
    component.addToCart(PEN);
    component.form.patchValue({ billDiscountPercent: 20 });
    settle();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="bill-discount-error"]')?.textContent).toContain('cannot exceed 5%');
    expect(component.completeSaleDisabledReason()).toContain('cannot exceed 5%');
  });

  it('puts a save-time cap error on the bill discount control', () => {
    const { component, fixture, el } = setup({ ...BILL, create: () => throwError(() => problem({ billDiscountPercent: ['Bill discount cannot exceed 5%.'] })) });
    component.addToCart(PEN);
    component.form.patchValue({ billDiscountPercent: 3 });
    settle();
    component.submit();
    fixture.detectChanges();
    expect(component.form.controls.billDiscountPercent.errors?.['server']).toBe('Bill discount cannot exceed 5%.');
    expect(el.querySelector('[data-testid="bill-discount-error"]')?.textContent).toContain('cannot exceed 5%');
  });

  it('shows a 403 on the bill discount (permission lost) without a generic banner', () => {
    const { component, fixture, el } = setup({ ...BILL, create: () => throwError(() => new HttpErrorResponse({ status: 403, error: { status: 403, title: 'You are not allowed to give a bill discount.' } })) });
    component.addToCart(PEN);
    component.form.patchValue({ billDiscountPercent: 3 });
    settle();
    component.submit();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="bill-discount-error"]')?.textContent).toContain('not allowed');
    expect(component.serverError()).toBeNull();
  });
});

describe('POS save errors (TP-4)', () => {
  const problem = (errors: Record<string, string[]>) => new HttpErrorResponse({ status: 400, error: { title: 'Validation', errors } });

  it('maps every lines[i].* error onto its cart row and shows it under the row', () => {
    const { component, fixture, el } = setup({
      create: () => throwError(() => problem({ 'lines[0].qty': ['Qty must be whole.'], 'lines[1].discount': ['Discount cannot be more than the line amount.'], customerId: ['Customer is inactive.'] })),
    });
    component.addToCart(PEN);
    component.addToCart(RICE);
    settle();
    component.submit();
    fixture.detectChanges();
    expect(component.lines.at(0).controls.qty.errors?.['server']).toBe('Qty must be whole.');
    expect(component.lines.at(1).controls.discount.errors?.['server']).toContain('Discount cannot');
    expect(component.form.controls.customerId.errors?.['server']).toBe('Customer is inactive.');
    const shown = Array.from(el.querySelectorAll('[data-testid="line-server-error"]')).map((e) => e.textContent!.trim());
    expect(shown).toEqual(['Qty must be whole.', 'Discount cannot be more than the line amount.']);
    expect(component.serverError()).toBe('Fix the highlighted fields and try again.');
  });

  it('reloads the carted products after a "price is now" error', () => {
    const { component, catalog } = setup({ create: () => throwError(() => problem({ 'lines[0].unitPrice': ['The price of Pen (Box) is now 95.00.'] })) });
    component.addToCart(PEN);
    settle();
    const calls = catalog.mock.calls.length;
    component.submit();
    expect(catalog.mock.calls.length).toBeGreaterThan(calls);
    expect(component.serverError()).toContain('is now 95.00');
  });

  it('shows a lines error (stock shortage, rate changed) as the banner and returns every field error at once', () => {
    const { component } = setup({ create: () => throwError(() => problem({ lines: ['Only 3 pc of Pen in stock.'], billCharges: ['Bill charges cannot have more than 2 decimals.'] })) });
    component.addToCart(PEN);
    settle();
    component.submit();
    expect(component.serverError()).toBe('Only 3 pc of Pen in stock.');
    expect(component.form.controls.billCharges.errors?.['server']).toContain('2 decimals');
  });

  it('on success carries the create warnings to the detail screen and clears nothing else', () => {
    const { component } = setup({
      create: () => of({ id: 'inv-1', invoiceNo: 'INV-1', warnings: [{ field: 'lines[0].hsnCode', message: 'Add an HSN code.' }] }),
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate');
    component.addToCart(PEN);
    settle();
    component.submit();
    expect(navigate).toHaveBeenCalledWith(['/sales', 'inv-1'], { state: { warnings: ['Item 1: Add an HSN code.'] } });
  });
});
