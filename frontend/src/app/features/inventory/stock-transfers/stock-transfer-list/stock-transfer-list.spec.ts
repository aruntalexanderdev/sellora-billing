import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../../../core/auth/auth.service';
import { provideScopeStubs } from '../../../../shared/scope/scope-testing';
import { StockTransferList } from './stock-transfer-list';
import { StockTransfersService } from '../stock-transfers.service';
import type { StockTransferListItem } from '../stock-transfers.models';

const branch = { id: 'b1', code: 'B1', name: 'Branch 1' };
function item(over: Partial<StockTransferListItem> = {}): StockTransferListItem {
  return {
    id: 't1', transferNo: 'TRF-0001', status: 'Dispatched', createdAt: '2026-11-06T10:00:00Z', branch,
    from: { id: 's', code: 'STORE', name: 'Main Store', type: 'BranchStore' }, to: { id: 'v', code: 'VAN1', name: 'Van 1', type: 'Vehicle' },
    sourceBranch: branch, destinationBranch: { id: 'b2', code: 'B2', name: 'Branch 2' }, lineCount: 3, outstandingLineCount: 2,
    dispatchedAt: null, completedAt: null, ...over,
  };
}

function setup(permissions: string[] = ['StockTransfers.View']) {
  const list = vi.fn(() => of({ items: [item(), item({ id: 't2', transferNo: 'TRF-0002', status: 'Completed', outstandingLineCount: 0 })], total: 2, page: 1, pageSize: 20 }));
  TestBed.configureTestingModule({
    imports: [StockTransferList],
    providers: [
      provideRouter([]),
      ...provideScopeStubs(),
      { provide: StockTransfersService, useValue: { list } },
      { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(StockTransferList);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, list, navigate };
}

const lastQuery = (list: ReturnType<typeof setup>['list']) => (list.mock.calls.at(-1) as unknown as [Record<string, unknown>])[0];

describe('StockTransferList', () => {
  it('shows from/to, branch labels, line counts and outstanding line counts', () => {
    const { el } = setup();
    const rows = Array.from(el.querySelectorAll('tbody tr')).map((r) => r.textContent ?? '');
    expect(rows[0]).toContain('TRF-0001');
    expect(rows[0]).toContain('Main Store (STORE)');
    expect(rows[0]).toContain('Van 1 (VAN1)');
    expect(rows[0]).toContain('B1 → B2');
    expect(rows[0]).toContain('2 lines');
    expect(rows[1]).toContain('—');
  });

  it('loads page 1 without filters, then turns each filter into a query param and resets paging', () => {
    const { component, list } = setup();
    expect(lastQuery(list)).toMatchObject({ page: 1, pageSize: 20, status: undefined, direction: undefined });

    component.onPageChange(2);
    expect(lastQuery(list)['page']).toBe(2);

    component.onStatusChange('Dispatched');
    expect(lastQuery(list)).toMatchObject({ status: 'Dispatched', page: 1 });
    component.onDirectionChange('incoming');
    component.onSourceChange('s1');
    component.onDestinationChange('d1');
    component.onDateFromChange('2026-11-01');
    component.onDateToChange('2026-11-30');
    expect(lastQuery(list)).toMatchObject({
      status: 'Dispatched', direction: 'incoming', sourceLocationId: 's1', destinationLocationId: 'd1', dateFrom: '2026-11-01', dateTo: '2026-11-30', page: 1,
    });

    component.clearFilters();
    expect(lastQuery(list)).toMatchObject({ status: 'Dispatched', direction: undefined, sourceLocationId: undefined, dateFrom: undefined });
    component.onStatusChange('All statuses');
    expect(lastQuery(list)['status']).toBeUndefined();
  });

  it('ignores an unknown direction value', () => {
    const { component, list } = setup();
    component.onDirectionChange('sideways');
    expect(lastQuery(list)['direction']).toBeUndefined();
  });

  it('hides New transfer without StockTransfers.Create', () => {
    expect(setup(['StockTransfers.View']).el.textContent).not.toContain('+ New transfer');
  });

  it('offers New transfer with StockTransfers.Create', () => {
    const { el, component, navigate } = setup(['StockTransfers.View', 'StockTransfers.Create']);
    expect(el.textContent).toContain('+ New transfer');
    component.goToCreate();
    expect(navigate).toHaveBeenCalledWith(['/inventory/stock-transfers/new']);
  });

  it('opens the detail', () => {
    const { component, navigate } = setup();
    component.openDetail('t1');
    expect(navigate).toHaveBeenCalledWith(['/inventory/stock-transfers', 't1']);
  });
});
