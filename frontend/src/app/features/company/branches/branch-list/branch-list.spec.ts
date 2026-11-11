import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ContextService } from '../../../../core/context/context.service';
import { ToastService } from '../../../../shared/ui/toast/toast.service';
import type { DataTableCell } from '../../../../shared/ui/data-table/data-table.types';
import type { Branch } from '../branches.models';
import { BranchesService } from '../branches.service';
import { BranchList } from './branch-list';

const branch = (o: Partial<Branch> = {}): Branch => ({
  id: 'b1', code: 'NTH', name: 'North', addressLine: null, city: 'Pune', state: null, phone: null, email: null, gstin: null,
  invoicePrefix: null, effectiveInvoicePrefix: 'INV-NTH', isDefault: false, isActive: true, ...o,
});

function setup(perms: string[], items: Branch[] = [branch()]) {
  const service = { list: vi.fn(() => of(items)), setStatus: vi.fn(() => of(branch())) };
  TestBed.configureTestingModule({
    imports: [BranchList],
    providers: [
      { provide: BranchesService, useValue: service },
      { provide: ContextService, useValue: { hasPermission: (c: string) => perms.includes(c) } },
      { provide: ToastService, useValue: { show: vi.fn() } },
      { provide: Router, useValue: { navigate: vi.fn() } },
    ],
  });
  const component = TestBed.createComponent(BranchList).componentInstance;
  return { component, service };
}

const actionLabels = (cells: DataTableCell[]) => {
  const a = cells.find((c) => c.kind === 'actions');
  return a && a.kind === 'actions' ? a.actions.map((x) => x.label) : [];
};

describe('BranchList', () => {
  it('loads the list and reloads with includeInactive', () => {
    const { component, service } = setup([]);
    component.ngOnInit();
    expect(service.list).toHaveBeenCalledWith(false);
    expect(component.state()).toBe('ready');
    expect(component.rows()).toHaveLength(1);
    component.setIncludeInactive(true);
    expect(service.list).toHaveBeenLastCalledWith(true);
  });

  it('shows the error state when loading fails', () => {
    const { component, service } = setup([]);
    service.list.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.ngOnInit();
    expect(component.state()).toBe('error');
  });

  it('hides every action without permission', () => {
    const { component } = setup([]);
    component.ngOnInit();
    expect(actionLabels(component.rows()[0].cells)).toEqual([]);
    expect(component.canCreate()).toBe(false);
  });

  it('offers Edit and Deactivate with the matching permissions', () => {
    const { component } = setup(['Company.Branches.Edit', 'Company.Branches.Disable']);
    component.ngOnInit();
    expect(actionLabels(component.rows()[0].cells)).toEqual(['Edit', 'Deactivate']);
  });

  it('does not offer Deactivate for the active default branch', () => {
    const { component } = setup(['Company.Branches.Disable'], [branch({ isDefault: true })]);
    component.ngOnInit();
    expect(actionLabels(component.rows()[0].cells)).toEqual([]);
  });

  it('D14: asks for a reason, posts it with the status; a server refusal is shown verbatim', async () => {
    const { component, service } = setup(['Company.Branches.Disable']);
    const msg = 'This branch\'s store still holds stock or has open transfers.';
    service.setStatus.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 400, error: { status: 400, errors: { IsActive: [msg] } } })),
    );
    component.toggle(branch());
    expect(component.statusTarget()?.id).toBe('b1');
    component.confirmStatus(null); // blocked without a reason
    expect(service.setStatus).not.toHaveBeenCalled();
    component.confirmStatus('Closing the site');
    expect(service.setStatus).toHaveBeenCalledWith('b1', false, 'Closing the site');
    expect(component.notice()).toBe(msg);
    expect(component.statusTarget()).toBeNull();
  });

  it('keeps the prompt open with the message when the server rejects the reason (400 on reason)', () => {
    const { component, service } = setup(['Company.Branches.Disable']);
    service.setStatus.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 400, error: { status: 400, errors: { reason: ['Reason is too long.'] } } })),
    );
    component.toggle(branch());
    component.confirmStatus('x');
    expect(component.statusError()).toBe('Reason is too long.');
    expect(component.statusTarget()).not.toBeNull();
    expect(component.notice()).toBeNull();
  });

  it('does nothing when the reason prompt is cancelled', () => {
    const { component, service } = setup(['Company.Branches.Disable']);
    component.toggle(branch());
    component.cancelStatus();
    expect(component.statusTarget()).toBeNull();
    expect(service.setStatus).not.toHaveBeenCalled();
  });
});
