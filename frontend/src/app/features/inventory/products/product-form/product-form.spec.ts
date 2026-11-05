import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { ProductForm } from './product-form';
import { ProductsService } from '../products.service';
import { UnitsService } from '../../../masters/units/units.service';
import { TaxesService } from '../../../masters/taxes/taxes.service';
import type { MeasurementCategory, UnitItem } from '../../../masters/units/units.models';
import type { ProductDetail, ProductUnit } from '../products.models';

function u(id: string, measurementCategory: MeasurementCategory, isActive = true): UnitItem {
  return { id, name: id, shortCode: id, measurementCategory, decimalPlaces: 0, standardFactor: null, isActive, isInUse: false, canDeactivate: true };
}

const UNITS: UnitItem[] = [
  u('kg', 'Weight'),
  u('pc', 'Count'),
  u('box', 'Packaging'),
  u('old-pc', 'Count', false),
  u('old-box', 'Packaging', false),
  u('m', 'Length'),
];

function pu(overrides: Partial<ProductUnit>): ProductUnit {
  return {
    id: 'pu-base', unitId: 'kg', unitCode: 'kg', unitName: 'kg', unitCategory: 'Weight', decimalPlaces: 3,
    innerProductUnitId: null, innerUnitId: null, innerQuantity: 1, conversionFactorToBase: 1, isBaseUnit: true,
    label: null, displayName: 'kg', barcode: null, isPurchasable: true, isSellable: true,
    isDefaultPurchaseUnit: false, isDefaultSalesUnit: false, sortOrder: 0, depth: 0, isActive: true,
    isLocked: false, lockReason: null,
    ...overrides,
  };
}

function product(baseUnitId: string, extra: Partial<ProductDetail> = {}): ProductDetail {
  return {
    id: 'p1', sku: 'SKU', name: 'Legacy', category: null, baseUnitId, baseUnitLabel: baseUnitId,
    taxId: 't1', taxLabel: 'GST 5%', taxRatePercent: 5, reorderLevel: 0, isActive: true,
    isBaseUnitLocked: false, units: [pu({ unitId: baseUnitId })],
    ...extra,
  };
}

function setup(id: string | null, baseUnitId = 'kg', detail: ProductDetail = product(baseUnitId)) {
  TestBed.configureTestingModule({
    imports: [ProductForm],
    providers: [
      { provide: UnitsService, useValue: { list: () => of(UNITS) } },
      { provide: TaxesService, useValue: { list: () => of([{ id: 't1', name: 'GST 5%', ratePercent: 5, isActive: true }]) } },
      { provide: ProductsService, useValue: { get: () => of(detail) } },
      { provide: Router, useValue: { navigateByUrl: vi.fn() } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } } },
    ],
  });
  // Not rendered: only the base-unit option filtering is under test.
  const component = TestBed.createComponent(ProductForm).componentInstance;
  component.ngOnInit();
  return component;
}

const ids = (units: UnitItem[]) => units.map((x) => x.id);

describe('ProductForm base-unit options', () => {
  it('new product: only active, non-Packaging units', () => {
    const component = setup(null);

    expect(ids(component.units())).toEqual(['kg', 'pc', 'm']);
  });

  it('edit: keeps the current base unit even when it is inactive', () => {
    const component = setup('p1', 'old-pc');

    expect(ids(component.units())).toEqual(['kg', 'pc', 'old-pc', 'm']);
    expect(component.form.controls.baseUnitId.value).toBe('old-pc');
  });

  it('edit: keeps a legacy Packaging base unit selectable so the form does not blank', () => {
    const component = setup('p1', 'old-box');

    expect(ids(component.units())).toEqual(['kg', 'pc', 'old-box', 'm']);
  });

  it('edit: active base unit is listed once', () => {
    const component = setup('p1', 'kg');

    expect(ids(component.units())).toEqual(['kg', 'pc', 'm']);
  });
});

describe('ProductForm units', () => {
  const box = pu({
    id: 'pu-box', unitId: 'box', unitName: 'box', unitCategory: 'Packaging', isBaseUnit: false,
    innerProductUnitId: 'pu-base', innerUnitId: 'kg', innerQuantity: 25, conversionFactorToBase: 25, sortOrder: 1, depth: 1,
  });

  it('new product: starts with a base row bound to the selected base unit', () => {
    const component = setup(null);

    expect(component.unitRows.length).toBe(1);
    component.form.controls.baseUnitId.setValue('pc');

    const base = component.unitRows.at(0).getRawValue();
    expect(base.isBaseUnit).toBe(true);
    expect(base.unitId).toBe('pc');
  });

  it('changing the base unit re-points unlocked rows that contained the old base', () => {
    const component = setup('p1', 'kg', product('kg', { units: [box, pu({})] }));

    component.form.controls.baseUnitId.setValue('m');

    expect(component.unitRows.at(0).controls.unitId.value).toBe('m');
    expect(component.unitRows.at(1).controls.innerUnitId.value).toBe('m');
  });

  it('edit: loads units with the base row first and stays pristine', () => {
    const component = setup('p1', 'kg', product('kg', { units: [box, pu({})] }));

    expect(component.unitRows.controls.map((r) => r.controls.unitId.value)).toEqual(['kg', 'box']);
    expect(component.form.dirty).toBe(false);
    expect(component.form.controls.baseUnitId.enabled).toBe(true);
  });

  it('edit: disables the base unit select when isBaseUnitLocked', () => {
    const component = setup('p1', 'kg', product('kg', { isBaseUnitLocked: true }));

    expect(component.isBaseUnitLocked()).toBe(true);
    expect(component.form.controls.baseUnitId.disabled).toBe(true);
    expect(component.buildRequest().baseUnitId).toBe('kg');
  });

  it('buildRequest sends units in row order with the base row first', () => {
    const component = setup('p1', 'kg', product('kg', { units: [box, pu({})] }));

    const request = component.buildRequest();

    expect(request.units).toEqual([
      expect.objectContaining({ id: 'pu-base', unitId: 'kg', innerUnitId: null, innerQuantity: 1, isActive: true }),
      expect.objectContaining({ id: 'pu-box', unitId: 'box', innerUnitId: 'kg', innerQuantity: 25 }),
    ]);
    expect(request).not.toHaveProperty('sellingModes');
  });

  it('maps multi-error ProblemDetails keys onto unit row controls', () => {
    const component = setup('p1', 'kg', product('kg', { units: [box, pu({})] }));

    component.applyServerErrors({
      title: 'Validation failed',
      errors: {
        'Units[1].InnerQuantity': ['Must equal a whole number of kg.'],
        'Units[1]': ['Row problem.'],
        Units: ['Only one default sales unit.'],
        BaseUnitId: ['Base unit cannot change.'],
        Mystery: ['Unmapped.'],
      },
    });

    const row = component.unitRows.at(1);
    expect(row.controls.innerQuantity.errors?.['server']).toBe('Must equal a whole number of kg.');
    expect(row.errors?.['server']).toBe('Row problem.');
    expect(component.unitRows.errors?.['server']).toBe('Only one default sales unit.');
    expect(component.form.controls.baseUnitId.errors?.['server']).toBe('Base unit cannot change.');
    expect(component.serverError()).toBe('Unmapped.');
  });
});
