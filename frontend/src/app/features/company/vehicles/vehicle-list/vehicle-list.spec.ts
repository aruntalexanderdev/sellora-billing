import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ContextService } from '../../../../core/context/context.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import type { DataTableCell } from '../../../../shared/ui/data-table/data-table.types';
import type { Vehicle } from '../vehicles.models';
import { VehiclesService } from '../vehicles.service';
import { VehicleList } from './vehicle-list';

const veh = (o: Partial<Vehicle> = {}): Vehicle => ({
  id: 'v1', stockLocationId: 'l1', vehicleNumber: 'KA-01-1234', vehicleName: 'Van 1', vehicleType: 'Van', assignedBranchId: null,
  assignedBranchCode: null, assignedBranchName: null, isActive: true, createdAt: '2026-01-01T00:00:00Z', ...o,
});
const labels = (cells: DataTableCell[]) => {
  const a = cells.find((c) => c.kind === 'actions');
  return a && a.kind === 'actions' ? a.actions.map((x) => x.label) : [];
};

function setup(perms: string[]) {
  const service = { list: vi.fn(() => of([veh()])), setStatus: vi.fn(() => of(veh())) };
  TestBed.configureTestingModule({
    imports: [VehicleList],
    providers: [
      { provide: VehiclesService, useValue: service },
      { provide: ContextService, useValue: { hasPermission: (c: string) => perms.includes(c) } },
      { provide: ConfirmDialogService, useValue: { ask: vi.fn(() => Promise.resolve(true)) } },
      { provide: Router, useValue: { navigate: vi.fn() } },
    ],
  });
  return { component: TestBed.createComponent(VehicleList).componentInstance, service };
}

describe('VehicleList', () => {
  it('loads, shows unassigned vehicles as Central, and applies filters', () => {
    const { component, service } = setup([]);
    component.ngOnInit();
    expect(component.state()).toBe('ready');
    const branchCell = component.rows()[0].cells[3];
    expect(branchCell.kind === 'text' && branchCell.text).toBe('Central');
    component.setType('Truck');
    component.setUnassigned(true);
    expect(service.list).toHaveBeenLastCalledWith({ assignedBranchId: null, unassigned: true, vehicleType: 'Truck', includeInactive: false });
  });

  it('error state on failure', () => {
    const { component, service } = setup([]);
    service.list.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.ngOnInit();
    expect(component.state()).toBe('error');
  });

  it('actions are hidden without permission and shown with it', () => {
    const none = setup([]);
    none.component.ngOnInit();
    expect(labels(none.component.rows()[0].cells)).toEqual([]);
    TestBed.resetTestingModule();
    const all = setup(['Company.Vehicles.Edit', 'Company.Vehicles.Disable']);
    all.component.ngOnInit();
    expect(labels(all.component.rows()[0].cells)).toEqual(['Edit', 'Deactivate']);
  });

  it('shows the InUse refusal on deactivation', async () => {
    const { component, service } = setup(['Company.Vehicles.Disable']);
    service.setStatus.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { IsActive: ['This vehicle still holds stock or has open transfers.'] } } })),
    );
    component.toggle(veh());
    component.confirmStatus('Reason given');
    expect(component.notice()).toBe('This vehicle still holds stock or has open transfers.');
  });
});
