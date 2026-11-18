import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { provideScopeStubs } from '../../../../shared/scope/scope-testing';
import type { TransitItem } from '../../shipments/shipments.models';
import { TransitService } from '../transit.service';
import { TransitPage } from './transit-page';

const loc = (id: string) => ({ id, code: id.toUpperCase(), name: id, type: 'BranchStore' });
const item = (over: Partial<TransitItem> = {}): TransitItem => ({
  consignmentId: 'c1', consignmentNo: 'CON-1', status: 'PartiallyReceived', direction: 'outgoing', shipmentId: 's1', shipmentNo: 'SHP-1', shipmentStatus: 'PartiallyDelivered',
  transportMode: 'Road', transportReference: 'TR-9', source: loc('store'), destination: loc('van'), destinationBranch: { id: 'b', code: 'B', name: 'B' },
  dispatchedAt: '2026-11-16T10:00:00Z', receiptCount: 2, unpostedReceiptCount: 1, hasDiscrepancy: true,
  lines: [{
    consignmentLineId: 'cl1', productId: 'p', productSku: 'PEN', productName: 'Pen', baseUnitCode: 'pc', unitCode: 'box', dispatchedBaseQty: 50, acceptedBaseQty: 30,
    damagedBaseQty: 5, rejectedBaseQty: 0, missingBaseQty: 5, postedBaseQty: 20, outstandingBaseQty: 10, inTransitBaseQty: 30, dispositionedDamagedBaseQty: 0, dispositionedRejectedBaseQty: 0, dispositionedMissingBaseQty: 0, openDiscrepancyBaseQty: 0,
  }],
  ...over,
});

function setup(items: TransitItem[] = [item(), item({ consignmentId: 'c2', consignmentNo: 'CON-2', hasDiscrepancy: false, unpostedReceiptCount: 0 })]) {
  const list = vi.fn((_q: unknown) => of({ items, total: items.length, page: 1, pageSize: 20 }));
  TestBed.configureTestingModule({
    imports: [TransitPage],
    providers: [provideRouter([]), ...provideScopeStubs(), { provide: TransitService, useValue: { list } }],
  });
  const fixture = TestBed.createComponent(TransitPage);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const lastQuery = () => list.mock.calls.at(-1)![0] as { view: string; search?: string; page: number };
  return { fixture, component: fixture.componentInstance, el, list, lastQuery };
}

describe('TransitPage', () => {
  it('has the five view tabs and starts on Current', () => {
    const { el, lastQuery } = setup();
    const tabs = Array.from(el.querySelectorAll('[role="tab"]')).map((t) => t.textContent?.trim());
    expect(tabs).toEqual(['Current', 'Incoming', 'Outgoing', 'Completed', 'Discrepancies']);
    expect(lastQuery().view).toBe('current');
  });

  it('each tab reloads with its view from page 1', () => {
    const { fixture, el, component, lastQuery } = setup();
    component.page.set(3);
    for (const v of ['incoming', 'outgoing', 'completed', 'discrepancies']) {
      (el.querySelector(`[data-testid="tab-${v}"]`) as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(lastQuery()).toMatchObject({ view: v, page: 1 });
    }
  });

  it('search reloads with the term and paging advances', () => {
    const { fixture, component, el, lastQuery } = setup();
    const input = el.querySelector('[data-testid="search"]') as HTMLInputElement;
    input.value = 'CON-2';
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(lastQuery()).toMatchObject({ view: 'current', search: 'CON-2', page: 1 });
    component.total.set(45);
    component.next();
    expect(lastQuery().page).toBe(2);
  });

  it('badges only discrepancy rows, links to the consignment and expands to the lines', () => {
    const { fixture, el } = setup();
    expect(el.querySelectorAll('[data-testid="transit-row"]')).toHaveLength(2);
    expect(el.querySelectorAll('[data-testid="discrepancy-badge"]')).toHaveLength(1);
    expect(el.querySelector('[data-testid="consignment-link"]')?.getAttribute('href')).toBe('/inventory/consignments/c1');
    expect(el.querySelector('[data-testid="unposted"]')?.textContent).toContain('1 not posted');
    expect(el.querySelector('[data-testid="transit-lines"]')).toBeNull();
    (el.querySelector('[data-testid="expand"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const rows = el.querySelectorAll('[data-testid="transit-line"]');
    expect(rows).toHaveLength(1);
    expect(rows[0].textContent).toContain('Pen');
    expect(rows[0].textContent).toContain('30');
  });

  it('shows an empty state per view', () => {
    const { el } = setup([]);
    expect(el.querySelector('[data-testid="empty"]')?.textContent).toContain('current');
  });
});
