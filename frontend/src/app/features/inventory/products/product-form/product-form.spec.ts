import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { ProductForm } from './product-form';
import { ProductCategoriesService } from '../../../../shared/product-categories/product-categories.service';
import { ProductsService } from '../products.service';
import { UnitsService } from '../../../masters/units/units.service';
import { TaxesService } from '../../../masters/taxes/taxes.service';
import { AuthService } from '../../../../core/auth/auth.service';
import type { MeasurementCategory, UnitItem } from '../../../masters/units/units.models';
import type { ProductDetail, ProductUnit } from '../products.models';
import type { ProductCategory } from '../../../../shared/product-categories/product-categories.models';

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
    id: 'p1', sku: 'SKU', name: 'Legacy', category: null, categoryId: null, categoryName: null, subcategoryId: null, subcategoryName: null, baseUnitId, baseUnitLabel: baseUnitId,
    taxId: 't1', taxLabel: 'GST 5%', taxRatePercent: 5, reorderLevel: 0, isActive: true,
    isBaseUnitLocked: false, units: [pu({ unitId: baseUnitId })],
    ...extra,
  };
}

function setup(id: string | null, baseUnitId = 'kg', detail: ProductDetail = product(baseUnitId), canManageCategories = false) {
  TestBed.configureTestingModule({
    imports: [ProductForm],
    providers: [
      { provide: UnitsService, useValue: { list: () => of(UNITS) } },
      { provide: ProductCategoriesService, useValue: { list: () => of([]) } },
      { provide: TaxesService, useValue: { list: () => of([{ id: 't1', name: 'GST 5%', ratePercent: 5, isActive: true }]) } },
      { provide: ProductsService, useValue: { get: () => of(detail) } },
      { provide: AuthService, useValue: { hasPermission: (code: string) => canManageCategories && code === 'Inventory.Categories' } },
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

describe('ProductForm category mapping', () => {
  const cats = [
    { id: 'c1', code: 'A', name: 'Grains', sortOrder: 0, isActive: true, productCount: 1, subcategories: [
      { id: 's1', categoryId: 'c1', code: 'R', name: 'Rice', sortOrder: 0, isActive: true, productCount: 1 },
      { id: 's2', categoryId: 'c1', code: 'O', name: 'Old', sortOrder: 1, isActive: false, productCount: 0 },
    ] },
    { id: 'c2', code: 'B', name: 'Retired', sortOrder: 1, isActive: false, productCount: 0, subcategories: [] },
  ];

  function withCategories(detail: ProductDetail, id: string | null = 'p1') {
    const c = setup(id, 'kg', detail);
    c.allCategories.set(cats);
    return c;
  }

  it('lists active categories only, plus the current inactive one', () => {
    expect(withCategories(product('kg'), null).categoryOptions().map((x) => x.id)).toEqual(['c1']);
    TestBed.resetTestingModule();
    const c = withCategories(product('kg', { categoryId: 'c2', categoryName: 'Retired' }));
    expect(c.categoryOptions().map((x) => x.id)).toEqual(['c1', 'c2']);
  });

  it('subcategory options follow the category and keep a current inactive subcategory', () => {
    const c = withCategories(product('kg', { categoryId: 'c1', subcategoryId: 's2' }));
    expect(c.subcategoryOptions().map((x) => x.id)).toEqual(['s1', 's2']);
    c.form.controls.categoryId.setValue('c2');
    expect(c.form.controls.subcategoryId.value).toBe('');
    expect(c.subcategoryOptions()).toEqual([]);
  });

  it('buildRequest sends ids (null when empty) and no legacy category', () => {
    const c = withCategories(product('kg', { categoryId: 'c1', subcategoryId: 's1' }));
    expect(c.buildRequest()).toMatchObject({ categoryId: 'c1', subcategoryId: 's1' });
    expect('category' in c.buildRequest()).toBe(false);
    // Clearing the category keeps the subcategory, which implies its category.
    c.form.controls.categoryId.setValue('');
    expect(c.buildRequest()).toMatchObject({ categoryId: 'c1', subcategoryId: 's1' });
    c.form.controls.subcategoryId.setValue('');
    c.form.controls.categoryId.setValue('');
    expect(c.buildRequest()).toMatchObject({ categoryId: null, subcategoryId: null });
  });

  it('maps server field errors onto the category controls', () => {
    const c = withCategories(product('kg'));
    c.applyServerErrors({ errors: { categoryId: ['Category is inactive.'], subcategoryId: ['Does not belong to the category.'] } });
    expect(c.form.controls.categoryId.errors?.['server']).toBe('Category is inactive.');
    expect(c.form.controls.subcategoryId.errors?.['server']).toBe('Does not belong to the category.');
    expect(c.serverError()).toBeNull();
  });
});

describe('ProductForm category / subcategory pickers', () => {
  const cats: ProductCategory[] = [
    { id: 'c1', code: 'G', name: 'Grains', sortOrder: 0, isActive: true, productCount: 0, subcategories: [
      { id: 's1', categoryId: 'c1', code: 'R', name: 'Rice', sortOrder: 0, isActive: true, productCount: 0 },
      { id: 's2', categoryId: 'c1', code: 'O', name: 'Others', sortOrder: 1, isActive: true, productCount: 0 },
      { id: 's3', categoryId: 'c1', code: 'X', name: 'Old', sortOrder: 2, isActive: false, productCount: 0 },
    ] },
    { id: 'c2', code: 'D', name: 'Dairy', sortOrder: 1, isActive: true, productCount: 0, subcategories: [
      { id: 's4', categoryId: 'c2', code: 'O', name: 'Others', sortOrder: 0, isActive: true, productCount: 0 },
    ] },
    { id: 'c3', code: 'Z', name: 'Retired', sortOrder: 2, isActive: false, productCount: 0, subcategories: [
      { id: 's5', categoryId: 'c3', code: 'Z', name: 'Gone', sortOrder: 0, isActive: true, productCount: 0 },
    ] },
  ];

  function fresh(canManage = false) {
    const c = setup(null, 'kg', product('kg'), canManage);
    c.allCategories.set(cats);
    return c;
  }
  const values = (c: ProductForm) => c.subcategoryComboOptions().map((o) => `${o.label}${o.hint ? ' · ' + o.hint : ''}`);

  it('no category: lists every active subcategory of active categories, labelled with the category', () => {
    const c = fresh();
    expect(values(c)).toEqual(['Rice · Grains', 'Others · Grains', 'Others · Dairy']);
  });

  it('selecting a subcategory sets its category and narrows the list', () => {
    const c = fresh();
    c.form.controls.subcategoryId.setValue('s4');
    expect(c.form.controls.categoryId.value).toBe('c2');
    expect(c.form.controls.categoryId.dirty).toBe(true);
    expect(values(c)).toEqual(['Others']);
    // Picking a subcategory of another category (via the full list) switches the category.
    c.form.controls.subcategoryId.setValue('s1');
    expect(c.form.controls.categoryId.value).toBe('c1');
    expect(c.form.controls.subcategoryId.value).toBe('s1');
  });

  it('selecting a category filters and clears a subcategory of another category', () => {
    const c = fresh();
    c.form.controls.subcategoryId.setValue('s2');
    c.form.controls.categoryId.setValue('c1');
    expect(c.form.controls.subcategoryId.value).toBe('s2');
    c.form.controls.categoryId.setValue('c2');
    expect(c.form.controls.subcategoryId.value).toBe('');
    expect(values(c)).toEqual(['Others']);
  });

  it('clearing the category restores the full list and keeps the subcategory', () => {
    const c = fresh();
    c.form.controls.subcategoryId.setValue('s1');
    c.form.controls.categoryId.setValue('');
    expect(c.form.controls.subcategoryId.value).toBe('s1');
    expect(values(c)).toEqual(['Rice · Grains', 'Others · Grains', 'Others · Dairy']);
  });

  it('edit: an inactive current subcategory still displays, marked inactive', () => {
    const c = setup('p1', 'kg', product('kg', { categoryId: 'c1', subcategoryId: 's3' }));
    c.allCategories.set(cats);
    expect(c.form.controls.subcategoryId.value).toBe('s3');
    expect(values(c)).toEqual(['Rice', 'Others', 'Old (inactive)']);
  });

  it('"+ New subcategory" is shown only with Inventory.Categories and opens with the selected category', () => {
    expect(fresh(false).canManageCategories()).toBe(false);
    TestBed.resetTestingModule();
    const c = fresh(true);
    expect(c.canManageCategories()).toBe(true);
    c.openNewSubcategory();
    expect(c.subcategoryEditor()).toEqual({ kind: 'subcategory', categoryId: null, id: null });
    c.form.controls.categoryId.setValue('c2');
    c.openNewSubcategory();
    expect(c.subcategoryEditor()).toEqual({ kind: 'subcategory', categoryId: 'c2', id: null });
  });

  it('a created subcategory is added to the list and selected (setting its category)', () => {
    const c = fresh(true);
    c.openNewSubcategory();
    const sub = { id: 's9', categoryId: 'c2', code: 'CH', name: 'Cheese', sortOrder: 5, isActive: true, productCount: 0 };
    c.onSubcategoryCreated({ kind: 'subcategory', created: true, subcategory: sub });
    expect(c.subcategoryEditor()).toBeNull();
    expect(c.form.controls.subcategoryId.value).toBe('s9');
    expect(c.form.controls.categoryId.value).toBe('c2');
    expect(values(c)).toEqual(['Others', 'Cheese']);
    expect(c.hasUnsavedChanges()).toBe(true);
  });

  it('a created subcategory under a category missing from the list still sends its category', () => {
    const c = fresh(true);
    const sub = { id: 's10', categoryId: 'c-new', code: 'N', name: 'New', sortOrder: 0, isActive: true, productCount: 0 };
    c.onSubcategoryCreated({ kind: 'subcategory', created: true, subcategory: sub });
    expect(c.buildRequest()).toMatchObject({ categoryId: 'c-new', subcategoryId: 's10' });
  });
});
