import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { UnitForm } from './unit-form';
import { UnitsService } from '../units.service';
import type { UnitItem } from '../units.models';

function unit(overrides: Partial<UnitItem> = {}): UnitItem {
  return {
    id: 'u-kg',
    name: 'Kilogram',
    shortCode: 'kg',
    measurementCategory: 'Weight',
    decimalPlaces: 3,
    standardFactor: 1,
    isActive: true,
    isInUse: false,
    ...overrides,
  };
}

function setup(id: string | null = null) {
  const unitsService = {
    get: vi.fn(() => of(unit())),
    create: vi.fn(() => of(unit())),
    update: vi.fn(() => of(unit())),
    list: vi.fn(() => of([])),
    setActive: vi.fn(() => of(undefined)),
  };
  const router = { navigateByUrl: vi.fn(() => Promise.resolve(true)) };
  TestBed.configureTestingModule({
    imports: [UnitForm],
    providers: [
      { provide: UnitsService, useValue: unitsService },
      { provide: Router, useValue: router },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } } },
    ],
  });
  // Logic-only tests: the component is never rendered (no detectChanges), so the template's RouterLink is not exercised.
  const component = TestBed.createComponent(UnitForm).componentInstance;
  return { component, unitsService, router, controls: component.form.controls };
}

describe('UnitForm', () => {
  describe('category rules (new unit)', () => {
    it('starts as Count with decimals 0 and factor null, both disabled', () => {
      const { component, controls } = setup();

      expect(component.isEdit()).toBe(false);
      expect(controls.measurementCategory.value).toBe('Count');
      expect(controls.measurementCategory.enabled).toBe(true);
      expect(controls.decimalPlaces.value).toBe(0);
      expect(controls.decimalPlaces.disabled).toBe(true);
      expect(controls.standardFactor.value).toBeNull();
      expect(controls.standardFactor.disabled).toBe(true);
      expect(component.isMeasurable()).toBe(false);
    });

    it('Count -> Weight defaults decimals to 3 and enables decimals and factor', () => {
      const { component, controls } = setup();

      controls.measurementCategory.setValue('Weight');

      expect(controls.decimalPlaces.value).toBe(3);
      expect(controls.decimalPlaces.enabled).toBe(true);
      expect(controls.standardFactor.enabled).toBe(true);
      expect(component.isMeasurable()).toBe(true);
      expect(component.referenceUnit()).toBe('kg');
    });

    it('Packaging -> Length defaults decimals to 3', () => {
      const { component, controls } = setup();
      controls.measurementCategory.setValue('Packaging');

      controls.measurementCategory.setValue('Length');

      expect(controls.decimalPlaces.value).toBe(3);
      expect(component.referenceUnit()).toBe('m');
    });

    it('Weight -> Packaging clears and disables the factor and forces decimals 0', () => {
      const { component, controls } = setup();
      controls.measurementCategory.setValue('Weight');
      controls.standardFactor.setValue(0.001);
      controls.decimalPlaces.setValue(2);

      controls.measurementCategory.setValue('Packaging');

      expect(controls.standardFactor.value).toBeNull();
      expect(controls.standardFactor.disabled).toBe(true);
      expect(controls.decimalPlaces.value).toBe(0);
      expect(controls.decimalPlaces.disabled).toBe(true);
      expect(component.referenceUnit()).toBeNull();
    });

    it('Weight -> Volume keeps the chosen decimals', () => {
      const { component, controls } = setup();
      controls.measurementCategory.setValue('Weight');
      controls.decimalPlaces.setValue(1);

      controls.measurementCategory.setValue('Volume');

      expect(controls.decimalPlaces.value).toBe(1);
      expect(controls.decimalPlaces.enabled).toBe(true);
      expect(component.referenceUnit()).toBe('L');
    });

    it('editing an unused unit: Count -> Weight does not overwrite decimals with the default', () => {
      const { component, controls } = setup('u-pc');
      component.loadUnit(unit({ id: 'u-pc', measurementCategory: 'Count', decimalPlaces: 0, standardFactor: null }));

      controls.measurementCategory.setValue('Weight');

      expect(component.isEdit()).toBe(true);
      expect(controls.decimalPlaces.value).toBe(0);
      expect(controls.decimalPlaces.enabled).toBe(true);
      expect(controls.standardFactor.enabled).toBe(true);
    });
  });

  describe('loadUnit', () => {
    it('in-use unit locks category, decimals and factor but not name/code, and leaves the form pristine', () => {
      const { component, controls } = setup('u-kg');

      component.loadUnit(unit({ isInUse: true }));

      expect(component.isInUse()).toBe(true);
      expect(controls.measurementCategory.disabled).toBe(true);
      expect(controls.decimalPlaces.disabled).toBe(true);
      expect(controls.standardFactor.disabled).toBe(true);
      expect(controls.name.enabled).toBe(true);
      expect(controls.shortCode.enabled).toBe(true);
      expect(component.form.pristine).toBe(true);
      expect(component.hasUnsavedChanges()).toBe(false);
      expect(component.form.getRawValue()).toEqual({
        name: 'Kilogram',
        shortCode: 'kg',
        measurementCategory: 'Weight',
        decimalPlaces: 3,
        standardFactor: 1,
      });
    });

    it('unused measurable unit keeps the loaded decimals and factor and stays editable', () => {
      const { component, controls } = setup('u-g');

      component.loadUnit(unit({ id: 'u-g', name: 'Gram', shortCode: 'g', decimalPlaces: 0, standardFactor: 0.001 }));

      expect(controls.measurementCategory.enabled).toBe(true);
      expect(controls.decimalPlaces.enabled).toBe(true);
      expect(controls.standardFactor.enabled).toBe(true);
      expect(controls.decimalPlaces.value).toBe(0);
      expect(controls.standardFactor.value).toBe(0.001);
      expect(component.form.pristine).toBe(true);
    });

    it('unused Count unit keeps decimals and factor disabled', () => {
      const { component, controls } = setup('u-pc');

      component.loadUnit(unit({ measurementCategory: 'Count', decimalPlaces: 0, standardFactor: null }));

      expect(controls.measurementCategory.enabled).toBe(true);
      expect(controls.decimalPlaces.disabled).toBe(true);
      expect(controls.standardFactor.disabled).toBe(true);
    });

    it('ngOnInit in edit mode loads the unit from the service', () => {
      const { component, unitsService, controls } = setup('u-kg');
      unitsService.get.mockReturnValue(of(unit({ name: 'Kilo', isInUse: true })));

      component.ngOnInit();

      expect(unitsService.get).toHaveBeenCalledWith('u-kg');
      expect(controls.name.value).toBe('Kilo');
      expect(controls.measurementCategory.disabled).toBe(true);
    });

    it('ngOnInit in edit mode shows "Unit not found." on 404', () => {
      const { component, unitsService } = setup('missing');
      unitsService.get.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 404 })));

      component.ngOnInit();

      expect(component.serverError()).toBe('Unit not found.');
    });

    it('ngOnInit for a new unit does not call the service', () => {
      const { component, unitsService } = setup();

      component.ngOnInit();

      expect(unitsService.get).not.toHaveBeenCalled();
    });
  });

  describe('buildRequest', () => {
    it('sends a null factor and 0 decimals for Count', () => {
      const { component, controls } = setup();
      controls.name.setValue('  Dozen ');
      controls.shortCode.setValue(' dz ');

      expect(component.buildRequest()).toEqual({
        name: 'Dozen',
        shortCode: 'dz',
        measurementCategory: 'Count',
        decimalPlaces: 0,
        standardFactor: null,
      });
    });

    it('keeps the factor and decimals for Weight', () => {
      const { component, controls } = setup();
      controls.name.setValue('Gram');
      controls.shortCode.setValue('g');
      controls.measurementCategory.setValue('Weight');
      controls.decimalPlaces.setValue(0);
      controls.standardFactor.setValue(0.001);

      const request = component.buildRequest();

      expect(request.decimalPlaces).toBe(0);
      expect(request.standardFactor).toBe(0.001);
    });

    it('sends an empty factor input as null', () => {
      const { component, controls } = setup();
      controls.measurementCategory.setValue('Volume');
      // A cleared number input yields '' at runtime.
      controls.standardFactor.setValue('' as unknown as number);

      expect(component.buildRequest().standardFactor).toBeNull();
    });

    it('coerces string inputs from number fields to numbers', () => {
      const { component, controls } = setup();
      controls.measurementCategory.setValue('Weight');
      controls.decimalPlaces.setValue('2' as unknown as number);
      controls.standardFactor.setValue('0.5' as unknown as number);

      const request = component.buildRequest();

      expect(request.decimalPlaces).toBe(2);
      expect(request.standardFactor).toBe(0.5);
    });

    it('includes the locked values of an in-use unit (disabled controls)', () => {
      const { component } = setup('u-kg');
      component.loadUnit(unit({ isInUse: true }));

      expect(component.buildRequest()).toEqual({
        name: 'Kilogram',
        shortCode: 'kg',
        measurementCategory: 'Weight',
        decimalPlaces: 3,
        standardFactor: 1,
      });
    });
  });

  describe('validators', () => {
    it.each([0, -1, -0.001])('rejects a standard factor of %s', (value) => {
      const { controls } = setup();
      controls.measurementCategory.setValue('Weight');

      controls.standardFactor.setValue(value);

      expect(controls.standardFactor.hasError('positive')).toBe(true);
    });

    it.each([null, 0.001, 1, 1_000_000_000])('accepts a standard factor of %s', (value) => {
      const { controls } = setup();
      controls.measurementCategory.setValue('Weight');

      controls.standardFactor.setValue(value);

      expect(controls.standardFactor.valid).toBe(true);
    });

    it('limits name to 60 and short code to 10 characters', () => {
      const { controls } = setup();

      controls.name.setValue('x'.repeat(60));
      controls.shortCode.setValue('y'.repeat(10));
      expect(controls.name.valid).toBe(true);
      expect(controls.shortCode.valid).toBe(true);

      controls.name.setValue('x'.repeat(61));
      controls.shortCode.setValue('y'.repeat(11));
      expect(controls.name.hasError('maxlength')).toBe(true);
      expect(controls.shortCode.hasError('maxlength')).toBe(true);
    });

    it('requires name and short code', () => {
      const { component, controls } = setup();

      expect(controls.name.hasError('required')).toBe(true);
      expect(controls.shortCode.hasError('required')).toBe(true);
      expect(component.form.invalid).toBe(true);
    });
  });

  describe('applyServerErrors', () => {
    it('maps a PascalCase field error onto its control', () => {
      const { component, controls } = setup();
      controls.measurementCategory.setValue('Weight');

      component.applyServerErrors({ errors: { StandardFactor: ['Standard factor must be greater than 0.'] } });

      expect(controls.standardFactor.errors).toEqual({ server: 'Standard factor must be greater than 0.' });
      expect(controls.standardFactor.touched).toBe(true);
      expect(component.serverError()).toBeNull();
    });

    it('maps ShortCode (duplicate) and camelCase keys onto their controls', () => {
      const { component, controls } = setup();

      component.applyServerErrors({ errors: { ShortCode: ['Duplicate.'], decimalPlaces: ['Bad decimals.'] } });

      expect(controls.shortCode.errors).toEqual({ server: 'Duplicate.' });
      expect(controls.decimalPlaces.errors).toEqual({ server: 'Bad decimals.' });
      expect(component.serverError()).toBeNull();
    });

    it('shows a field with no matching control (IsActive) in serverError', () => {
      const { component } = setup();

      component.applyServerErrors({ errors: { IsActive: ['This unit is used by products and cannot be deactivated.'] } });

      expect(component.serverError()).toBe('This unit is used by products and cannot be deactivated.');
    });

    it('shows the title when there are no field errors', () => {
      const { component } = setup();

      component.applyServerErrors({ title: 'Conflict', status: 409 });

      expect(component.serverError()).toBe('Conflict');
    });

    it('falls back to a generic message when the problem is empty', () => {
      const { component } = setup();

      component.applyServerErrors(undefined);

      expect(component.serverError()).toBe('Something went wrong.');
    });
  });

  describe('submit', () => {
    it('does not call the service when the form is invalid', () => {
      const { component, unitsService } = setup();

      component.submit();

      expect(unitsService.create).not.toHaveBeenCalled();
      expect(component.form.controls.name.touched).toBe(true);
    });

    it('creates a new unit and navigates back to the list', () => {
      const { component, unitsService, router, controls } = setup();
      controls.name.setValue('Dozen');
      controls.shortCode.setValue('dz');
      controls.name.markAsDirty();

      component.submit();

      expect(unitsService.create).toHaveBeenCalledWith({
        name: 'Dozen',
        shortCode: 'dz',
        measurementCategory: 'Count',
        decimalPlaces: 0,
        standardFactor: null,
      });
      expect(router.navigateByUrl).toHaveBeenCalledWith('/administration/units');
      expect(component.hasUnsavedChanges()).toBe(false);
    });

    it('updates an existing unit by id', () => {
      const { component, unitsService } = setup('u-kg');
      component.loadUnit(unit({ isInUse: true }));
      component.form.controls.name.setValue('Kilo');

      component.submit();

      expect(unitsService.update).toHaveBeenCalledWith('u-kg', expect.objectContaining({ name: 'Kilo', decimalPlaces: 3, standardFactor: 1 }));
    });

    it('maps a 400 response onto the matching control and stays on the page', () => {
      const { component, unitsService, router, controls } = setup('u-kg');
      component.loadUnit(unit());
      unitsService.update.mockReturnValue(
        throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { DecimalPlaces: ['Locked.'] } } })),
      );

      component.submit();

      expect(controls.decimalPlaces.errors).toEqual({ server: 'Locked.' });
      expect(component.saving()).toBe(false);
      expect(router.navigateByUrl).not.toHaveBeenCalled();
    });
  });
});
