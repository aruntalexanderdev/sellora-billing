import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { BranchesService } from '../../branches/branches.service';
import type { Warehouse } from '../warehouses.models';
import { WarehousesService } from '../warehouses.service';
import { WarehouseForm } from './warehouse-form';

const wh: Warehouse = {
  id: 'w1', stockLocationId: 'l1', code: 'WH1', name: 'Main', ownership: 'Branch', branchId: 'b1', branchCode: 'NTH', branchName: 'North',
  isActive: true, createdAt: '2026-01-01T00:00:00Z',
};

function setup(id: string | null = null) {
  const service = { get: vi.fn(() => of(wh)), create: vi.fn(() => of(wh)), update: vi.fn(() => of(wh)) };
  TestBed.configureTestingModule({
    imports: [WarehouseForm],
    providers: [
      { provide: WarehousesService, useValue: service },
      { provide: BranchesService, useValue: { accessible: vi.fn(() => of([])) } },
      { provide: Router, useValue: { navigateByUrl: vi.fn() } },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } } },
    ],
  });
  const component = TestBed.createComponent(WarehouseForm).componentInstance;
  return { component, service, c: component.form.controls };
}

describe('WarehouseForm', () => {
  it('creates a company warehouse when no branch is chosen (branchId null)', () => {
    const { component, service, c } = setup();
    c.code.setValue('WH-2');
    c.name.setValue('Overflow');
    component.submit();
    expect(service.create).toHaveBeenCalledWith({ code: 'WH-2', name: 'Overflow', branchId: null });
  });

  it('creates a branch warehouse with the chosen branch', () => {
    const { component, service, c } = setup();
    c.code.setValue('WH3');
    c.name.setValue('Back');
    c.branchId.setValue('b1');
    component.submit();
    expect(service.create).toHaveBeenCalledWith({ code: 'WH3', name: 'Back', branchId: 'b1' });
  });

  it('rejects a malformed code client-side', () => {
    const { component, service, c } = setup();
    c.code.setValue('WH--1');
    c.name.setValue('X');
    component.submit();
    expect(c.code.errors?.['pattern']).toBeTruthy();
    expect(service.create).not.toHaveBeenCalled();
  });

  it('edit sends only the name; code and branch are locked', () => {
    const { component, service, c } = setup('w1');
    component.ngOnInit();
    expect(c.code.disabled).toBe(true);
    expect(c.branchId.disabled).toBe(true);
    c.name.setValue('Renamed');
    component.submit();
    expect(service.update).toHaveBeenCalledWith('w1', { name: 'Renamed' });
  });

  it('maps server field errors onto controls', () => {
    const { component, service, c } = setup();
    service.create.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { Code: ['This code is already used by a stock location.'] } } })),
    );
    c.code.setValue('WH1');
    c.name.setValue('X');
    component.submit();
    expect(c.code.errors?.['server']).toContain('already used');
    expect(component.serverError()).toBeNull();
  });
});
