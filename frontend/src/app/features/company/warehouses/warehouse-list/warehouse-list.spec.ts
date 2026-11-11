import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ContextService } from '../../../../core/context/context.service';
import { ConfirmDialogService } from '../../../../shared/ui/confirm-dialog/confirm-dialog.service';
import type { DataTableCell } from '../../../../shared/ui/data-table/data-table.types';
import type { Warehouse } from '../warehouses.models';
import { WarehousesService } from '../warehouses.service';
import { WarehouseList } from './warehouse-list';

const wh = (o: Partial<Warehouse> = {}): Warehouse => ({
  id: 'w1', stockLocationId: 'l1', code: 'WH1', name: 'Main WH', ownership: 'Company', branchId: null, branchCode: null, branchName: null,
  isActive: true, createdAt: '2026-01-01T00:00:00Z', ...o,
});
const labels = (cells: DataTableCell[]) => {
  const a = cells.find((c) => c.kind === 'actions');
  return a && a.kind === 'actions' ? a.actions.map((x) => x.label) : [];
};

function setup(perms: string[]) {
  const service = { list: vi.fn(() => of([wh()])), setStatus: vi.fn(() => of(wh())) };
  TestBed.configureTestingModule({
    imports: [WarehouseList],
    providers: [
      { provide: WarehousesService, useValue: service },
      { provide: ContextService, useValue: { hasPermission: (c: string) => perms.includes(c) } },
      { provide: ConfirmDialogService, useValue: { ask: vi.fn(() => Promise.resolve(true)) } },
      { provide: Router, useValue: { navigate: vi.fn() } },
    ],
  });
  return { component: TestBed.createComponent(WarehouseList).componentInstance, service };
}

describe('WarehouseList', () => {
  it('loads and passes filters to the API', () => {
    const { component, service } = setup([]);
    component.ngOnInit();
    expect(component.state()).toBe('ready');
    component.setOwnership('Branch');
    component.setBranch('b1');
    component.setIncludeInactive(true);
    expect(service.list).toHaveBeenLastCalledWith({ branchId: 'b1', ownership: 'Branch', includeInactive: true });
  });

  it('error state on failure', () => {
    const { component, service } = setup([]);
    service.list.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.ngOnInit();
    expect(component.state()).toBe('error');
  });

  it('actions follow permissions', () => {
    const none = setup([]);
    none.component.ngOnInit();
    expect(labels(none.component.rows()[0].cells)).toEqual([]);
    TestBed.resetTestingModule();
    const all = setup(['Company.Warehouses.Edit', 'Company.Warehouses.Disable']);
    all.component.ngOnInit();
    expect(labels(all.component.rows()[0].cells)).toEqual(['Edit', 'Deactivate']);
  });

  it('shows the server refusal when deactivation fails', async () => {
    const { component, service } = setup(['Company.Warehouses.Disable']);
    service.setStatus.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 400, error: { errors: { IsActive: ['This warehouse still holds stock or has open transfers.'] } } })),
    );
    component.toggle(wh());
    component.confirmStatus('Reason given');
    expect(component.notice()).toContain('still holds stock');
  });
});
