import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { StockAdjustmentForm } from './stock-adjustment-form';
import { StockAdjustmentsService } from '../stock-adjustments.service';
import { StockService } from '../../stock/stock.service';
import { ProductsService } from '../../products/products.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import type { ProductDetail, ProductUnit } from '../../products/products.models';
import type { StockAdjustmentDetail } from '../stock-adjustments.models';

function pu(overrides: Partial<ProductUnit>): ProductUnit {
  return {
    id: 'pu-pc', unitId: 'pc', unitCode: 'pc', unitName: 'Piece', unitCategory: 'Count', decimalPlaces: 0,
    innerProductUnitId: null, innerUnitId: null, innerQuantity: 1, conversionFactorToBase: 1, isBaseUnit: true,
    label: null, displayName: 'Piece', barcode: null, isPurchasable: true, isSellable: true,
    isDefaultPurchaseUnit: false, isDefaultSalesUnit: false, sortOrder: 0, depth: 0, isActive: true,
    isLocked: false, lockReason: null,
    ...overrides,
  };
}

const nonBase = (id: string, code: string, name: string, factor: number, extra: Partial<ProductUnit> = {}) =>
  pu({ id, unitId: code, unitCode: code, unitName: name, unitCategory: 'Packaging', displayName: name, isBaseUnit: false,
    innerProductUnitId: 'pu-pc', innerUnitId: 'pc', innerQuantity: factor, conversionFactorToBase: factor, ...extra });

// Pen: pc base, Box 10, Full Carton 100 (default purchase unit), plus an inactive old box.
const PEN: ProductDetail = {
  id: 'pen', sku: 'PEN-1', name: 'Pen', category: null, baseUnitId: 'pc', baseUnitLabel: 'pc',
  taxId: 't1', taxLabel: 'GST 5%', taxRatePercent: 5, reorderLevel: 0, isActive: true, isBaseUnitLocked: false,
  units: [
    pu({}),
    nonBase('pu-box', 'box', 'Box', 10, { sortOrder: 1 }),
    nonBase('pu-old', 'obox', 'Old Box', 12, { sortOrder: 2, isActive: false }),
    nonBase('pu-fctn', 'fctn', 'Full Carton', 100, { sortOrder: 3, isDefaultPurchaseUnit: true }),
  ],
};

function result(overrides: Partial<StockAdjustmentDetail> = {}): StockAdjustmentDetail {
  return {
    id: 'adj-1', adjustmentNo: 'ADJ-0001', createdAt: '2026-11-06T10:00:00Z', productId: 'pen', productName: 'Pen', sku: 'PEN-1',
    type: 'OpeningStock', direction: 'Increase', enteredQuantity: 3, productUnitId: 'pu-fctn', unitCode: 'fctn',
    unitDisplayName: 'Full Carton', conversionFactorToBase: 100, enteredDecimalPlaces: 0, quantityBaseUnits: 300,
    baseUnitCode: 'pc', baseDecimalPlaces: 0, enteredUnitCost: 250, unitCost: 2.5, reason: null, beforeQty: 0, afterQty: 300,
    userName: 'admin', batches: [],
    ...overrides,
  };
}

function setup(opts: { create?: () => unknown } = {}) {
  const create = vi.fn(opts.create ?? (() => of(result())));
  const toast = { show: vi.fn() };
  TestBed.configureTestingModule({
    imports: [StockAdjustmentForm],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({ productId: 'pen' }) } } },
      {
        provide: ProductsService,
        useValue: {
          list: () => of({ items: [{ id: 'pen', sku: 'PEN-1', name: 'Pen', isActive: true }], total: 1, page: 1, pageSize: 100 }),
          get: () => of(PEN),
        },
      },
      { provide: StockService, useValue: { detail: () => of({ quantityOnHand: 0, baseUnitLabel: 'pc' }) } },
      { provide: StockAdjustmentsService, useValue: { create } },
      { provide: ConfirmDialogService, useValue: { ask: () => Promise.resolve(true) } },
      { provide: ToastService, useValue: toast },
    ],
  });
  const router = TestBed.inject(Router);
  const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(StockAdjustmentForm);
  fixture.detectChanges();
  const component = fixture.componentInstance;
  const el: HTMLElement = fixture.nativeElement;
  const text = (testId: string) => el.querySelector(`[data-testid="${testId}"]`)?.textContent?.trim() ?? null;
  return { fixture, component, el, text, create, navigate, toast };
}

describe('StockAdjustmentForm', () => {
  it('lists only active units, in API order, and defaults to the base row', () => {
    const { component, el } = setup();

    expect(component.units().map((u) => u.id)).toEqual(['pu-pc', 'pu-box', 'pu-fctn']);
    expect(component.form.controls.productUnitId.value).toBe('pu-pc');
    const options = Array.from(el.querySelectorAll('[data-testid="unit-select"] option')).map((o) => (o as HTMLOptionElement).value);
    expect(options).toEqual(['pu-pc', 'pu-box', 'pu-fctn']);
  });

  it('switches step and validators with the unit: 1.5 Box is invalid, 1.5 kg-style decimals follow decimalPlaces', () => {
    const { component, fixture, el } = setup();
    const quantity = component.form.controls.quantity;

    component.form.controls.productUnitId.setValue('pu-box');
    quantity.setValue(1.5);
    fixture.detectChanges();
    expect(quantity.hasError('maxDecimalPlaces')).toBe(true);
    expect((el.querySelector('[data-testid="quantity-input"]') as HTMLInputElement).step).toBe('1');

    quantity.setValue(2);
    expect(quantity.valid).toBe(true);

    // Same product with a 3-dp base: re-point the base unit's decimals.
    component.units.update((list) => list.map((u) => (u.id === 'pu-pc' ? { ...u, decimalPlaces: 3 } : u)));
    component.form.controls.productUnitId.setValue('pu-pc');
    quantity.setValue(1.5);
    fixture.detectChanges();
    expect(quantity.valid).toBe(true);
    expect((el.querySelector('[data-testid="quantity-input"]') as HTMLInputElement).step).toBe('0.001');
    quantity.setValue(1.2345);
    expect(quantity.hasError('maxDecimalPlaces')).toBe(true);
  });

  it('previews "= 300 pc" for 3 Full Carton and the resulting stock in base units', () => {
    const { component, fixture, text } = setup();

    component.form.controls.productUnitId.setValue('pu-fctn');
    component.form.controls.quantity.setValue(3);
    component.form.controls.type.setValue('OpeningStock');
    fixture.detectChanges();

    expect(text('base-preview')).toBe('= 300 pc');
    expect(text('resulting-qty')).toBe('300 pc');
    expect(text('quantity-label')).toBe('Quantity (Full Carton)');
  });

  it('hides the base preview for the base unit', () => {
    const { component, fixture, text } = setup();

    component.form.controls.quantity.setValue(3);
    fixture.detectChanges();

    expect(text('base-preview')).toBeNull();
  });

  it('labels the cost per entered unit with a per-base hint', () => {
    const { component, fixture, text } = setup();

    component.form.controls.type.setValue('OpeningStock');
    component.form.controls.productUnitId.setValue('pu-fctn');
    component.form.controls.unitCost.setValue(250);
    fixture.detectChanges();

    expect(text('cost-label')).toBe('Unit cost per Full Carton (₹)');
    expect(text('cost-hint')).toBe('= ₹2.50 per pc');
  });

  it('posts the new payload shape and navigates to the stock detail', async () => {
    const { component, create, navigate, toast } = setup();

    component.form.controls.type.setValue('OpeningStock');
    component.form.controls.productUnitId.setValue('pu-fctn');
    component.form.controls.quantity.setValue(3);
    component.form.controls.unitCost.setValue(250);
    component.form.controls.reason.setValue('Initial count');
    await component.submit();

    expect(create).toHaveBeenCalledTimes(1);
    const [payload, key] = create.mock.calls[0] as unknown as [Record<string, unknown>, string];
    expect(payload).toEqual({
      productId: 'pen',
      productUnitId: 'pu-fctn',
      type: 'OpeningStock',
      direction: 'Increase',
      quantity: 3,
      unitCost: 250,
      reason: 'Initial count',
    });
    expect(payload).not.toHaveProperty('quantityBaseUnits');
    expect(typeof key).toBe('string');
    expect(toast.show).toHaveBeenCalledWith('ADJ-0001 posted · Pen: 0 → 300 pc.');
    expect(navigate).toHaveBeenCalledWith(['/inventory/stock', 'pen']);
  });

  it('does not send a unit cost for a decrease', async () => {
    const { component, create } = setup();

    component.form.controls.type.setValue('Damage');
    component.form.controls.quantity.setValue(2);
    component.form.controls.unitCost.setValue(9);
    await component.submit();

    const payload = (create.mock.calls[0] as unknown as [Record<string, unknown>])[0];
    expect(payload['direction']).toBe('Decrease');
    expect(payload['unitCost']).toBeNull();
  });

  it('maps server errors onto quantity and productUnitId', async () => {
    const problem = { title: 'Validation failed', errors: { Quantity: ['Box allows whole numbers only.'], ProductUnitId: ['Unit is inactive.'] } };
    const { component, fixture, el } = setup({
      create: () => throwError(() => new HttpErrorResponse({ status: 400, error: problem })),
    });

    component.form.controls.quantity.setValue(2);
    await component.submit();
    fixture.detectChanges();

    expect(component.form.controls.quantity.errors?.['server']).toBe('Box allows whole numbers only.');
    expect(component.form.controls.productUnitId.errors?.['server']).toBe('Unit is inactive.');
    expect(component.serverError()).toBeNull();
    expect(el.textContent).toContain('Box allows whole numbers only.');
    expect(el.textContent).toContain('Unit is inactive.');
  });
});
