import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { ProductForm } from './product-form';
import { ProductsService } from '../products.service';
import { UnitsService } from '../../../masters/units/units.service';
import { TaxesService } from '../../../masters/taxes/taxes.service';
import type { MeasurementCategory, UnitItem } from '../../../masters/units/units.models';
import type { ProductDetail } from '../products.models';

function u(id: string, measurementCategory: MeasurementCategory, isActive = true): UnitItem {
  return { id, name: id, shortCode: id, measurementCategory, decimalPlaces: 0, standardFactor: null, isActive, isInUse: false };
}

const UNITS: UnitItem[] = [
  u('kg', 'Weight'),
  u('pc', 'Count'),
  u('box', 'Packaging'),
  u('old-pc', 'Count', false),
  u('old-box', 'Packaging', false),
  u('m', 'Length'),
];

function product(baseUnitId: string): ProductDetail {
  return {
    id: 'p1', sku: 'SKU', name: 'Legacy', category: null, baseUnitId, baseUnitLabel: baseUnitId,
    taxId: 't1', taxLabel: 'GST 5%', taxRatePercent: 5, reorderLevel: 0, isActive: true, sellingModes: [],
  };
}

function setup(id: string | null, baseUnitId = 'kg') {
  TestBed.configureTestingModule({
    imports: [ProductForm],
    providers: [
      { provide: UnitsService, useValue: { list: () => of(UNITS) } },
      { provide: TaxesService, useValue: { list: () => of([{ id: 't1', name: 'GST 5%', ratePercent: 5, isActive: true }]) } },
      { provide: ProductsService, useValue: { get: () => of(product(baseUnitId)) } },
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
