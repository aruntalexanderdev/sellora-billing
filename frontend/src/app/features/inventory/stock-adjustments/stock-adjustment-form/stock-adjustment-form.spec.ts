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
import { signal } from '@angular/core';
import { AuthService } from '../../../../core/auth/auth.service';
import { ContextService } from '../../../../core/context/context.service';
import { StockLocationsService } from '../../../../shared/locations/stock-locations.service';
import type { StockLocation } from '../../../../shared/locations/locations.models';
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
  id: 'pen', sku: 'PEN-1', name: 'Pen', category: null, categoryId: null, categoryName: null, subcategoryId: null, subcategoryName: null, baseUnitId: 'pc', baseUnitLabel: 'pc',
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
    userName: 'admin', batches: [], branchId: 'b1', branchCode: 'MAIN', branchName: 'Main',
    stockLocationId: null, stockLocationCode: null, stockLocationName: null,
    ...overrides,
  };
}

function setup(opts: { create?: () => unknown; locations?: StockLocation[]; canViewLocations?: boolean; detailResult?: Record<string, unknown>; allBranches?: boolean; primaryBranchId?: string | null } = {}) {
  const detail = vi.fn((..._args: unknown[]) => of(opts.detailResult ?? { quantityOnHand: 0, baseUnitLabel: 'pc' }));
  const create = vi.fn(opts.create ?? (() => of(result())));
  const list = vi.fn((..._a: unknown[]) => of(opts.locations ?? []));
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
      { provide: StockService, useValue: { detail } },
      { provide: StockLocationsService, useValue: { list } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => (opts.canViewLocations ?? false) && c === 'StockLocations.View' } },
      { provide: ContextService, useValue: { contextVersion: signal(0).asReadonly(), workingBranch: signal(opts.allBranches ? null : { id: 'b1' }), primaryBranchId: signal(opts.primaryBranchId ?? null) } },
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
  return { fixture, component, el, text, create, navigate, toast, detail, list };
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
      stockLocationId: null,
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

  describe('location', () => {
    const loc = (id: string, type: StockLocation['locationType'], name: string): StockLocation => ({
      id, code: id.toUpperCase(), name, locationType: type, branchId: 'b1', branchCode: 'MAIN', branchName: 'Main', isActive: true,
      canSell: true, canReceivePurchase: true, isSystemManaged: false,
    });
    const LOCATIONS = [loc('store', 'BranchStore', 'Main Store'), loc('wh', 'BranchWarehouse', 'Back Warehouse')];

    it('shows the picker only with StockLocations.View and more than one location (O5)', () => {
      expect(setup({ canViewLocations: true, locations: LOCATIONS }).el.querySelector('[data-testid="adjust-location"] select')).not.toBeNull();
      TestBed.resetTestingModule();
      expect(setup({ canViewLocations: true, locations: [LOCATIONS[0]] }).el.querySelector('[data-testid="adjust-location"] select')).toBeNull();
      TestBed.resetTestingModule();
      expect(setup({ canViewLocations: false, locations: LOCATIONS }).el.querySelector('[data-testid="adjust-location"] select')).toBeNull();
    });

    describe('current quantity without a picker', () => {
  const bal = (locationId: string, type: string, qty: number) => ({ locationId, code: locationId.toUpperCase(), name: locationId, type, qty, avgCost: 1, value: qty });

      it('previews the branch store quantity, not the total across visible locations', () => {
        const { component, detail } = setup({
          canViewLocations: true, locations: [loc('store', 'BranchStore', 'Main Store')],
          detailResult: { quantityOnHand: 150, baseUnitLabel: 'pc', locations: [bal('store', 'BranchStore', 50), bal('van', 'Vehicle', 100)] },
        });
        TestBed.tick();
        expect(detail).toHaveBeenLastCalledWith('pen', null);
        expect(component.currentQty()).toBe(50);
      });

      it('FAIL CLOSED: without StockLocations.View the branch store cannot be verified, so there is no preview (even with one store)', () => {
        const { component, list } = setup({
          canViewLocations: false,
          detailResult: { quantityOnHand: 150, baseUnitLabel: 'pc', locations: [bal('store', 'BranchStore', 50), bal('van', 'Vehicle', 100)] },
        });
        TestBed.tick();
        expect(list).not.toHaveBeenCalledWith(expect.objectContaining({ type: 'BranchStore' }));
        expect(component.currentQty()).toBeNull();
        expect(component.needsBranch()).toBe(true);
      });

      it('FAIL CLOSED: a failing location list gives no preview instead of the first store', () => {
        const { component } = setup({
          canViewLocations: true, locations: [loc('store', 'BranchStore', 'Main Store')],
          detailResult: { quantityOnHand: 70, baseUnitLabel: 'pc', locations: [bal('s1', 'BranchStore', 20), bal('store', 'BranchStore', 50)] },
        });
        const list = TestBed.inject(StockLocationsService).list as unknown as ReturnType<typeof vi.fn>;
        list.mockImplementation(() => throwError(() => new Error('boom')));
        component.locationId.set(null);
        (component as unknown as { loadCurrentQty(p: string, l: string | null): void }).loadCurrentQty('pen', null);
        TestBed.tick();
        expect(component.currentQty()).toBeNull();
      });

      it('is 0 when the store holds none of the product (stock only in a vehicle)', () => {
        const { component } = setup({ canViewLocations: true, locations: [loc('store', 'BranchStore', 'Main Store')], detailResult: { quantityOnHand: 100, baseUnitLabel: 'pc', locations: [bal('van', 'Vehicle', 100)] } });
        TestBed.tick();
        expect(component.currentQty()).toBe(0);
      });

      it('picks the working branch store among several via the location list when the user may view locations', () => {
        const other = { ...loc('store2', 'BranchStore', 'North Store'), branchId: 'b2' };
        const { component } = setup({
          canViewLocations: true, locations: [loc('store', 'BranchStore', 'Main Store'), other].slice(0, 1),
          detailResult: { quantityOnHand: 70, baseUnitLabel: 'pc', locations: [bal('store2', 'BranchStore', 20), bal('store', 'BranchStore', 50)] },
        });
        TestBed.tick();
        expect(component.currentQty()).toBe(50);
      });

      it('with a chosen location uses that location total as before', () => {
        const { component } = setup({ canViewLocations: true, locations: LOCATIONS, detailResult: { quantityOnHand: 9, baseUnitLabel: 'pc', locations: [bal('store', 'BranchStore', 3)] } });
        TestBed.tick();
        expect(component.locationId()).toBe('store');
        expect(component.currentQty()).toBe(9);
      });
    });

    it('sends the chosen stockLocationId and reloads the current quantity at that location', async () => {
      const { component, create, detail } = setup({ canViewLocations: true, locations: LOCATIONS });
      TestBed.tick();
      expect(component.locationId()).toBe('store');
      component.locationId.set('wh');
      TestBed.tick();
      expect(detail).toHaveBeenLastCalledWith('pen', 'wh');
      component.form.controls.quantity.setValue(2);
      await component.submit();
      expect((create.mock.calls[0] as unknown as [Record<string, unknown>])[0]['stockLocationId']).toBe('wh');
    });

    it('maps a stockLocationId field error next to the picker', async () => {
      const problem = { title: 'Validation failed', errors: { StockLocationId: ['Location is inactive.'] } };
      const { component, fixture, el } = setup({
        canViewLocations: true,
        locations: LOCATIONS,
        create: () => throwError(() => new HttpErrorResponse({ status: 400, error: problem })),
      });
      component.form.controls.quantity.setValue(2);
      await component.submit();
      fixture.detectChanges();
      expect(component.locationError()).toBe('Location is inactive.');
      expect(el.querySelector('[data-testid="adjust-location"] .field-error')?.textContent).toContain('Location is inactive.');
      expect(component.serverError()).toBeNull();
    });
  });

  it('warns about unsaved changes until posted', async () => {
    const { component } = setup();
    expect(component.hasUnsavedChanges()).toBe(false);
    component.form.controls.quantity.markAsDirty();
    expect(component.hasUnsavedChanges()).toBe(true);
    component.form.controls.quantity.setValue(2);
    await component.submit();
    expect(component.hasUnsavedChanges()).toBe(false);
  });
});

describe('StockAdjustmentForm in All-branches mode (no working branch)', () => {
  const loc = (id: string, name: string, branchId: string): StockLocation => ({
    id, code: id.toUpperCase(), name, locationType: 'BranchStore', branchId, branchCode: 'X', branchName: 'X', isActive: true,
    canSell: true, canReceivePurchase: true, isSystemManaged: false,
  });
  const bal = (locationId: string, type: string, qty: number) => ({ locationId, code: locationId.toUpperCase(), name: locationId, type, qty, avgCost: 1, value: qty });

  it('without a picker and no location rows falls back to the visible total for the preview', () => {
    const { component, el } = setup({ allBranches: true, detailResult: { quantityOnHand: 42, baseUnitLabel: 'pc', locations: null } });
    TestBed.tick();
    expect(component.currentQty()).toBe(42);
    expect(el.textContent).toContain('Current stock');
  });

  it('with a single visible store previews that store, never the cross-location total', () => {
    const { component } = setup({ allBranches: true, primaryBranchId: 'b1', canViewLocations: true, locations: [loc('store', 'Main Store', 'b1')], detailResult: { quantityOnHand: 150, baseUnitLabel: 'pc', locations: [bal('store', 'BranchStore', 50), bal('van', 'Vehicle', 100)] } });
    TestBed.tick();
    expect(component.currentQty()).toBe(50);
  });

  it('CORRECTION: with several stores and no working branch it previews the PRIMARY branch store (not the first store)', () => {
    const { component, list } = setup({
      allBranches: true, canViewLocations: true, primaryBranchId: 'b1',
      locations: [loc('s2', 'Main Store', 'b1')], // one location row: no picker, so no preselected location
      detailResult: { quantityOnHand: 70, baseUnitLabel: 'pc', locations: [bal('s1', 'BranchStore', 20), bal('s2', 'BranchStore', 50)] },
    });
    TestBed.tick();
    expect(list.mock.calls.some((c) => (c[0] as { branchId?: string } | undefined)?.branchId === 'b1')).toBe(true);
    expect(component.currentQty()).toBe(50);
  });

  it('CORRECTION: with several stores and neither a working nor a primary branch it shows no preview ("Select a branch") instead of guessing the first store', () => {
    const { component, list, el } = setup({
      allBranches: true, canViewLocations: true, locations: [],
      detailResult: { quantityOnHand: 70, baseUnitLabel: 'pc', locations: [bal('s1', 'BranchStore', 20), bal('s2', 'BranchStore', 50)] },
    });
    TestBed.tick();
    expect(list).not.toHaveBeenCalledWith(expect.objectContaining({ type: 'BranchStore' }));
    expect(component.currentQty()).toBeNull();
    expect(el.querySelector('[data-testid="select-branch-hint"]')).not.toBeNull();
  });

  it('posts without stockLocationId when no picker is shown', async () => {
    const { component, create } = setup({ allBranches: true });
    component.form.controls.quantity.setValue(2);
    await component.submit();
    expect((create.mock.calls[0] as unknown as [Record<string, unknown>])[0]['stockLocationId'] ?? null).toBeNull();
  });
});
