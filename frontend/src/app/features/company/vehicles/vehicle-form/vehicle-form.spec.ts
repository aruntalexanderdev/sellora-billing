import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import { BranchesService } from '../../branches/branches.service';
import type { Vehicle } from '../vehicles.models';
import { VehiclesService } from '../vehicles.service';
import { VehicleForm } from './vehicle-form';

const veh: Vehicle = {
  id: 'v1', stockLocationId: 'l1', vehicleNumber: 'KA-01-1234', vehicleName: 'Van 1', vehicleType: 'Van', assignedBranchId: 'b1',
  assignedBranchCode: 'NTH', assignedBranchName: 'North', isActive: true, createdAt: '2026-01-01T00:00:00Z',
};

function setup(id: string | null = null) {
  const service = { get: vi.fn(() => of(veh)), create: vi.fn(() => of(veh)), update: vi.fn(() => of(veh)) };
  const router = { navigateByUrl: vi.fn() };
  TestBed.configureTestingModule({
    imports: [VehicleForm],
    providers: [
      { provide: VehiclesService, useValue: service },
      { provide: BranchesService, useValue: { accessible: vi.fn(() => of([])) } },
      { provide: Router, useValue: router },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } } },
    ],
  });
  const component = TestBed.createComponent(VehicleForm).componentInstance;
  return { component, service, router, c: component.form.controls };
}

describe('VehicleForm', () => {
  it('creates a central vehicle (assignedBranchId null)', () => {
    const { component, service, router, c } = setup();
    c.vehicleNumber.setValue('KA 01 1234');
    c.vehicleName.setValue('Van 1');
    c.vehicleType.setValue('Truck');
    component.submit();
    expect(service.create).toHaveBeenCalledWith({ vehicleNumber: 'KA 01 1234', vehicleName: 'Van 1', vehicleType: 'Truck', assignedBranchId: null });
    expect(router.navigateByUrl).toHaveBeenCalledWith('/company/vehicles');
  });

  it('edit locks the number and reassigns to central with null', () => {
    const { component, service, c } = setup('v1');
    component.ngOnInit();
    expect(c.vehicleNumber.disabled).toBe(true);
    c.assignedBranchId.setValue('');
    component.submit();
    expect(service.update).toHaveBeenCalledWith('v1', { vehicleName: 'Van 1', vehicleType: 'Van', assignedBranchId: null });
  });

  it('shows the server InUse refusal under the assigned-branch control', () => {
    const { component, service, c } = setup('v1');
    component.ngOnInit();
    const msg = 'This vehicle still holds stock or has open transfers. Transfer, return or reconcile its stock before reassigning it.';
    service.update.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { AssignedBranchId: [msg] } } })),
    );
    c.assignedBranchId.setValue('');
    component.submit();
    expect(c.assignedBranchId.errors?.['server']).toBe(msg);
    expect(component.serverError()).toBeNull();
  });

  it('maps VehicleNumber server errors and requires the name', () => {
    const { component, service, c } = setup();
    component.submit();
    expect(service.create).not.toHaveBeenCalled();
    expect(c.vehicleName.errors?.['required']).toBeTruthy();
    service.create.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { VehicleNumber: ['This number is already used by a vehicle or another stock location.'] } } })),
    );
    c.vehicleNumber.setValue('X1');
    c.vehicleName.setValue('V');
    component.submit();
    expect(c.vehicleNumber.errors?.['server']).toContain('already used');
  });
});
