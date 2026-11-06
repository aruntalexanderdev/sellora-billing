import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { Pos } from './pos';
import { SalesService } from '../sales.service';
import { CustomersService } from '../customers/customers.service';
import { CashBankService } from '../../accounts/cash-bank/cash-bank.service';
import { AuthService } from '../../../core/auth/auth.service';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import type { SaleCatalogProduct, SaleCatalogUnit } from '../sales.models';

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

function setup(opts: { permissions?: string[]; lookup?: (code: string) => unknown; create?: () => unknown } = {}) {
  const permissions = opts.permissions ?? [];
  const toast = { show: vi.fn() };
  const lookupBarcode = vi.fn(opts.lookup ?? (() => throwError(() => new HttpErrorResponse({ status: 404 }))));
  const create = vi.fn<(...args: unknown[]) => unknown>(opts.create ?? (() => of({ id: 'inv-1', invoiceNo: 'INV-1' })));
  TestBed.configureTestingModule({
    imports: [Pos],
    providers: [
      provideRouter([]),
      { provide: SalesService, useValue: { catalog: () => of([PEN, RICE]), lookupBarcode, create } },
      { provide: CustomersService, useValue: { list: () => of({ items: [{ id: 'c1', name: 'Walk-in', isActive: true, isWalkIn: true }] }) } },
      { provide: CashBankService, useValue: { options: () => of([]) } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
      { provide: ToastService, useValue: toast },
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
  return { fixture, component, el, toast, lookupBarcode, create, scan };
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
    component.submit();
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
    component.submit();
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
