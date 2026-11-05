import { TestBed } from '@angular/core/testing';
import { FormArray } from '@angular/forms';
import { Subject, of, throwError } from 'rxjs';
import {
  PREVIEW_DEBOUNCE_MS,
  ProductUnitsEditor,
  createBaseUnitRow,
  createUnitRow,
  problemKeyToFormPath,
  rowIndexOfField,
  toProductUnitInputs,
  unitRowFromDto,
  type UnitRowGroup,
} from './product-units-editor';
import { ProductsService } from '../../products.service';
import type { MeasurementCategory, UnitItem } from '../../../../masters/units/units.models';
import type { ProductUnit, UnitPreviewResponse } from '../../products.models';

function unit(id: string, name: string, category: MeasurementCategory, decimalPlaces = 0, standardFactor: number | null = null, isActive = true): UnitItem {
  return { id, name, shortCode: id, measurementCategory: category, decimalPlaces, standardFactor, isActive, isInUse: false, canDeactivate: true };
}

const UNITS: UnitItem[] = [
  unit('pc', 'Piece', 'Count'),
  unit('dz', 'Dozen', 'Count'),
  unit('box', 'Box', 'Packaging'),
  unit('hctn', 'Half Carton', 'Packaging'),
  unit('fctn', 'Full Carton', 'Packaging'),
  unit('old', 'Old Pack', 'Packaging', 0, null, false),
  unit('kg', 'Kilogram', 'Weight', 3, 1),
  unit('g', 'Gram', 'Weight', 3, 0.001),
  unit('l', 'Litre', 'Volume', 3, 1),
];

const EMPTY_PREVIEW: UnitPreviewResponse = { isValid: true, rows: [], errors: [], warnings: [] };

function penRows(): FormArray<UnitRowGroup> {
  return new FormArray<UnitRowGroup>([
    createBaseUnitRow('pc'),
    createUnitRow({ unitId: 'box', innerUnitId: 'pc', innerQuantity: 10 }),
    createUnitRow({ unitId: 'hctn', innerUnitId: 'box', innerQuantity: 5 }),
    createUnitRow({ unitId: 'fctn', innerUnitId: 'hctn', innerQuantity: 2 }),
  ]);
}

function dto(overrides: Partial<ProductUnit>): ProductUnit {
  return {
    id: 'pu-x', unitId: 'pc', unitCode: 'pc', unitName: 'Piece', unitCategory: 'Count', decimalPlaces: 0,
    innerProductUnitId: null, innerUnitId: null, innerQuantity: 1, conversionFactorToBase: 1, isBaseUnit: true,
    label: null, displayName: 'Piece', barcode: null, isPurchasable: true, isSellable: true,
    isDefaultPurchaseUnit: false, isDefaultSalesUnit: false, sortOrder: 0, depth: 0, isActive: true,
    isLocked: false, lockReason: null,
    ...overrides,
  };
}

function setup(units: FormArray<UnitRowGroup>, preview: UnitPreviewResponse = EMPTY_PREVIEW, productId: string | null = null) {
  const productsService = { previewUnits: vi.fn(() => of(preview)) };
  TestBed.configureTestingModule({
    imports: [ProductUnitsEditor],
    providers: [{ provide: ProductsService, useValue: productsService }],
  });
  const fixture = TestBed.createComponent(ProductUnitsEditor);
  fixture.componentRef.setInput('units', units);
  fixture.componentRef.setInput('allUnits', UNITS);
  fixture.componentRef.setInput('productId', productId);
  fixture.detectChanges();
  const component = fixture.componentInstance;
  const render = () => {
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  };
  return { component, fixture, units, productsService, render };
}

describe('ProductUnitsEditor', () => {
  afterEach(() => vi.useRealTimers());

  describe('hierarchy and preview text', () => {
    it('Pen: factors 1/10/50/100 and chains sorted by factor', () => {
      const { component, render } = setup(penRows());

      expect(component.rowFactors()).toEqual([1, 10, 50, 100]);
      expect(component.chains().map((c) => c.chain)).toEqual([
        '1 Piece',
        '1 Box = 10 Piece',
        '1 Half Carton = 5 Box = 50 Piece',
        '1 Full Carton = 2 Half Carton = 10 Box = 100 Piece',
      ]);
      const chains = [...render().querySelectorAll('.chain')].map((li) => li.textContent?.trim());
      expect(chains.at(-1)).toBe('1 Full Carton = 2 Half Carton = 10 Box = 100 Piece');
    });

    it('uses the label as display name in chains', () => {
      const units = penRows();
      units.at(1).controls.label.setValue('Box of 10');
      const { component } = setup(units);

      expect(component.chains()[1].chain).toBe('1 Box of 10 = 10 Piece');
    });

    it('excludes inactive rows from the chains', () => {
      const units = penRows();
      units.at(3).controls.isActive.setValue(false);
      const { component } = setup(units);

      expect(component.chains().map((c) => c.name)).toEqual(['Piece', 'Box', 'Half Carton']);
    });
  });

  describe('adding rows and option filtering', () => {
    it('add: new row defaults to containing the base unit', () => {
      const { component, units } = setup(penRows());

      component.addRow();

      expect(units.length).toBe(5);
      expect(units.at(4).controls.innerUnitId.value).toBe('pc');
      expect(units.dirty).toBe(true);
    });

    it('unit options: active, category allowed for a Count base, not the base, not used by another active row', () => {
      const { component } = setup(penRows());
      component.addRow();

      expect(component.unitOptions()[4].map((u) => u.id)).toEqual(['dz']);
    });

    it('unit options keep the row’s own unit and free a unit when its row is inactive', () => {
      const units = penRows();
      units.at(3).controls.isActive.setValue(false);
      const { component } = setup(units);
      component.addRow();

      expect(component.unitOptions()[1].map((u) => u.id)).toEqual(['dz', 'box', 'fctn']);
      expect(component.unitOptions()[4].map((u) => u.id)).toEqual(['dz', 'fctn']);
    });

    it('unit options for a Weight base exclude Count and Volume units', () => {
      const { component } = setup(new FormArray([createBaseUnitRow('kg'), createUnitRow({ innerUnitId: 'kg' })]));

      expect(component.unitOptions()[1].map((u) => u.id)).toEqual(['box', 'hctn', 'fctn', 'g']);
    });

    it('inner options exclude the row itself and rows whose chain contains it', () => {
      const { component } = setup(penRows());

      expect(component.innerOptions()[1].map((o) => o.unitId)).toEqual(['pc']);
      expect(component.innerOptions()[3].map((o) => o.unitId)).toEqual(['pc', 'box', 'hctn']);
    });

    it('re-parent: Full Carton contains 2 Box', () => {
      const { component, units } = setup(penRows());

      component.selectInner(3, 'box');

      expect(units.at(3).controls.innerUnitId.value).toBe('box');
      expect(component.rowFactors()[3]).toBe(20);
      expect(component.chains().find((c) => c.index === 3)?.chain).toBe('1 Full Carton = 2 Box = 20 Piece');
      // Sorted by factor: Full Carton (20) now comes before Half Carton (50).
      expect(component.chains().map((c) => c.name)).toEqual(['Piece', 'Box', 'Full Carton', 'Half Carton']);
    });

    it('suggests the inner quantity from standard factors (Gram in Kilogram = 0.001)', () => {
      const { component, units } = setup(new FormArray([createBaseUnitRow('kg'), createUnitRow({ innerUnitId: 'kg' })]));

      component.selectUnit(1, 'g');

      expect(units.at(1).controls.innerQuantity.value).toBe(0.001);
      expect(component.rowFactors()[1]).toBe(0.001);
    });

    it('does not suggest a quantity for packaging units', () => {
      const { component, units } = setup(new FormArray([createBaseUnitRow('kg'), createUnitRow({ innerUnitId: 'kg' })]));

      component.selectUnit(1, 'box');

      expect(units.at(1).controls.innerQuantity.value).toBeNull();
    });

    it('cannot remove a row another active row contains; can remove a leaf', () => {
      const { component, units } = setup(penRows());

      expect(component.removable()).toEqual([false, false, false, true]);
      component.removeRow(1);
      expect(units.length).toBe(4);
      component.removeRow(3);
      expect(units.length).toBe(3);
    });
  });

  describe('client-side rule hints', () => {
    it('R5: a Count base needs whole factors', () => {
      const units = penRows();
      units.at(1).controls.innerQuantity.setValue(2.5);
      const { component } = setup(units);

      expect(component.rowIssues()[1].join(' ')).toContain('whole number of Piece');
    });

    it('cycle is reported', () => {
      const units = penRows();
      units.at(1).controls.innerUnitId.setValue('fctn');
      const { component } = setup(units);

      expect(component.rowIssues()[1].join(' ')).toContain('cycle');
      expect(component.rowFactors()[1]).toBeNull();
    });

    it('standard factor mismatch (1 Gram = 0.002 kg)', () => {
      const { component } = setup(
        new FormArray([createBaseUnitRow('kg'), createUnitRow({ unitId: 'g', innerUnitId: 'kg', innerQuantity: 0.002 })]),
      );

      expect(component.rowIssues()[1]).toContain('1 Gram is 0.001 Kilogram.');
    });

    it('duplicate active unit', () => {
      const units = penRows();
      units.push(createUnitRow({ unitId: 'box', innerUnitId: 'pc', innerQuantity: 12 }));
      const { component } = setup(units);

      expect(component.rowIssues()[4].join(' ')).toContain('listed twice');
    });
  });

  describe('flags and defaults', () => {
    it('default purchase/sales are single-select', () => {
      const { component, units } = setup(penRows());

      component.setDefault(1, 'purchase', true);
      component.setDefault(3, 'purchase', true);
      component.setDefault(0, 'sales', true);

      expect(units.controls.map((r) => r.controls.isDefaultPurchaseUnit.value)).toEqual([false, false, false, true]);
      expect(units.controls.map((r) => r.controls.isDefaultSalesUnit.value)).toEqual([true, false, false, false]);
    });

    it('clearing purchasable / deactivating clears the matching default', () => {
      const { component, units } = setup(penRows());
      component.setDefault(1, 'purchase', true);
      component.setDefault(1, 'sales', true);

      component.setFlag(1, 'isPurchasable', false);
      expect(units.at(1).controls.isDefaultPurchaseUnit.value).toBe(false);
      expect(units.at(1).controls.isDefaultSalesUnit.value).toBe(true);

      component.setFlag(1, 'isActive', false);
      expect(units.at(1).controls.isDefaultSalesUnit.value).toBe(false);
      expect(component.canBeDefault(1, 'sales')).toBe(false);
    });

    it('the base row is always active', () => {
      const { component, units } = setup(penRows());

      component.setFlag(0, 'isActive', false);

      expect(units.at(0).controls.isActive.value).toBe(true);
    });
  });

  describe('locked rows', () => {
    function lockedPen(): FormArray<UnitRowGroup> {
      return new FormArray<UnitRowGroup>([
        unitRowFromDto(dto({ id: 'pu-pc', isLocked: true, lockReason: 'ContainedInLockedUnit' })),
        unitRowFromDto(
          dto({
            id: 'pu-box', unitId: 'box', unitName: 'Box', unitCategory: 'Packaging', isBaseUnit: false,
            innerProductUnitId: 'pu-pc', innerUnitId: 'pc', innerQuantity: 10, conversionFactorToBase: 10,
            barcode: '8901', isDefaultSalesUnit: true, sortOrder: 1, depth: 1, isLocked: true, lockReason: 'Prices',
          }),
        ),
      ]);
    }

    it('structural controls are disabled; label, barcode and flags stay editable', () => {
      const units = lockedPen();
      const box = units.at(1).controls;

      expect(box.unitId.disabled && box.innerUnitId.disabled && box.innerQuantity.disabled).toBe(true);
      expect(box.label.enabled && box.barcode.enabled && box.isSellable.enabled && box.isActive.enabled).toBe(true);
    });

    it('shows the lock reason and a Replace action, but no Replace on the base row', () => {
      const { render } = setup(lockedPen());
      const html = render();

      const notes = [...html.querySelectorAll('.lock-note')].map((n) => n.textContent ?? '');
      expect(notes[0]).toContain('Contained in a locked unit');
      expect(notes[0]).not.toContain('Replace');
      expect(notes[1]).toContain('Has selling prices');
      expect(notes[1]).toContain('Replace');
    });

    it('Replace deactivates the locked row and appends an editable copy', () => {
      const { component, units } = setup(lockedPen());

      component.replaceRow(1);

      const old = units.at(1).getRawValue();
      const copy = units.at(2);
      expect(old.isActive).toBe(false);
      expect(old.isDefaultSalesUnit).toBe(false);
      expect(old.id).toBe('pu-box');
      expect(copy.getRawValue()).toMatchObject({
        id: null, unitId: 'box', innerUnitId: 'pc', innerQuantity: 10, barcode: '8901',
        isDefaultSalesUnit: true, isLocked: false, isActive: true,
      });
      expect(copy.controls.innerQuantity.enabled).toBe(true);
      expect(units.dirty).toBe(true);

      copy.controls.innerQuantity.setValue(20);
      expect(component.chains().map((c) => c.chain)).toEqual(['1 Piece', '1 Box = 20 Piece']);
    });
  });

  describe('server preview', () => {
    const STALE: UnitPreviewResponse = {
      isValid: false, rows: [], errors: [{ field: 'Units[3].InnerQuantity', message: 'Stale error.' }], warnings: [],
    };
    const FRESH: UnitPreviewResponse = { isValid: true, rows: [], errors: [], warnings: [{ field: 'Units[1]', message: 'Fresh.' }] };

    it('a change during an in-flight request discards the stale response', () => {
      vi.useFakeTimers();
      const { component, productsService, units } = setup(penRows());
      const inFlight = new Subject<UnitPreviewResponse>();
      productsService.previewUnits.mockImplementationOnce(() => inFlight).mockImplementationOnce(() => of(FRESH));

      vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
      expect(productsService.previewUnits).toHaveBeenCalledTimes(1);

      component.removeRow(3);
      expect(inFlight.observed).toBe(false); // cancelled immediately, not at the next debounced emit
      inFlight.next(STALE);
      expect(component.preview()).toBeNull();
      expect(component.previewStatus()).toBe('pending');

      vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
      expect(productsService.previewUnits).toHaveBeenCalledTimes(2);
      expect(component.preview()).toEqual(FRESH);
      expect(component.rowPreviewErrors().flat()).toEqual([]);
      expect(units.length).toBe(3);
    });

    it('removing a row clears the previous result at once (indices shift)', () => {
      vi.useFakeTimers();
      const { component } = setup(penRows(), STALE);
      vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
      expect(component.previewErrors().length).toBe(1);

      component.removeRow(3);

      expect(component.preview()).toBeNull();
      expect(component.previewErrors()).toEqual([]);
    });

    it('a failed request clears the previous preview and shows a neutral status', () => {
      vi.useFakeTimers();
      const { component, productsService, units, render } = setup(penRows(), STALE);
      vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
      expect(component.previewErrors().length).toBe(1);

      productsService.previewUnits.mockImplementationOnce(() => throwError(() => new Error('500')));
      units.at(1).controls.label.setValue('Box of 10');
      vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);

      expect(component.previewStatus()).toBe('failed');
      expect(component.preview()).toBeNull();
      expect(component.previewErrors()).toEqual([]);
      expect(component.rowPreviewErrors().flat()).toEqual([]);
      expect(render().querySelector('.preview-status')?.textContent).toContain('Could not check units');
    });

    it('debounces changes into a single previewUnits call with the full request', () => {
      vi.useFakeTimers();
      const { productsService, units } = setup(penRows(), EMPTY_PREVIEW, 'p1');

      units.at(1).controls.label.setValue('B');
      units.at(1).controls.label.setValue('Box of 10');
      vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS - 1);
      expect(productsService.previewUnits).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1);

      expect(productsService.previewUnits).toHaveBeenCalledTimes(1);
      const request = (productsService.previewUnits.mock.calls[0] as unknown[])[0];
      expect(request).toMatchObject({
        productId: 'p1',
        baseUnitId: 'pc',
        units: [
          { unitId: 'pc', innerUnitId: null, innerQuantity: 1, isActive: true },
          { unitId: 'box', innerUnitId: 'pc', innerQuantity: 10, label: 'Box of 10' },
          { unitId: 'hctn', innerUnitId: 'box', innerQuantity: 5 },
          { unitId: 'fctn', innerUnitId: 'hctn', innerQuantity: 2 },
        ],
      });
    });

    it('does not call the server while a row is incomplete', () => {
      vi.useFakeTimers();
      const { component, productsService } = setup(penRows());

      component.addRow();
      vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);

      expect(productsService.previewUnits).not.toHaveBeenCalled();
      expect(component.previewStatus()).toBe('incomplete');
    });

    it('shows server errors and warnings against the right row', () => {
      vi.useFakeTimers();
      const preview: UnitPreviewResponse = {
        isValid: false,
        rows: [],
        errors: [
          { field: 'Units[2].InnerQuantity', message: 'Must be a whole number of Piece.' },
          { field: 'Units', message: 'Only one default sales unit.' },
        ],
        warnings: [{ field: 'Units[1]', message: 'Two units have the same factor.' }],
      };
      const { component, render } = setup(penRows(), preview);
      vi.advanceTimersByTime(PREVIEW_DEBOUNCE_MS);
      const html = render();

      expect(component.previewStatus()).toBe('ok');
      expect(component.previewErrors().map((e) => e.text)).toEqual([
        'Half Carton: Must be a whole number of Piece.',
        'Only one default sales unit.',
      ]);
      expect(component.previewWarnings().map((w) => w.text)).toEqual(['Box: Two units have the same factor.']);
      expect(component.rowPreviewErrors()[2]).toEqual(['Must be a whole number of Piece.']);
      expect(html.querySelector('.preview-errors')?.textContent).toContain('Half Carton: Must be a whole number of Piece.');
      expect(html.querySelector('.preview-status')?.textContent).toContain('Needs attention');
    });
  });

  describe('request mapping and error keys', () => {
    it('toProductUnitInputs: base row has no inner and quantity 1; blank label/barcode become null', () => {
      const units = penRows();
      units.at(1).controls.label.setValue('  ');
      units.at(1).controls.barcode.setValue(' 890 ');

      const [base, box] = toProductUnitInputs(units.getRawValue());

      expect(base).toEqual({
        id: null, unitId: 'pc', innerUnitId: null, innerQuantity: 1, label: null, barcode: null,
        isPurchasable: true, isSellable: true, isDefaultPurchaseUnit: false, isDefaultSalesUnit: false, isActive: true,
      });
      expect(box).toMatchObject({ innerUnitId: 'pc', innerQuantity: 10, label: null, barcode: '890' });
    });

    it.each([
      ['Units[2].InnerQuantity', 'units.2.innerQuantity'],
      ['Units[0]', 'units.0'],
      ['Units', 'units'],
      ['BaseUnitId', 'baseUnitId'],
      ['$.units[1].barcode', 'units.1.barcode'],
    ])('problemKeyToFormPath(%s) = %s', (key, path) => {
      expect(problemKeyToFormPath(key)).toBe(path);
    });

    it('rowIndexOfField', () => {
      expect(rowIndexOfField('Units[12].Label')).toBe(12);
      expect(rowIndexOfField('Units')).toBeNull();
      expect(rowIndexOfField('BaseUnitId')).toBeNull();
    });
  });
});
