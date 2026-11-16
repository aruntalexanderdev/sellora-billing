import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import type { ConsignmentDetail } from '../../shipments/shipments.models';
import { ConsignmentsService } from '../consignments.service';
import { ConsignmentDetailPage } from './consignment-detail';

const loc = (id: string, name: string) => ({ id, code: id.toUpperCase(), name, type: 'BranchStore' });
function detail(over: Partial<ConsignmentDetail> = {}): ConsignmentDetail {
  return {
    id: 'c1', consignmentNo: 'CON-0001', status: 'Loaded', direction: 'incoming', shipmentId: null, shipmentNo: 'SHP-0001', shipmentStatus: 'Loading',
    transportMode: 'Road', transportVia: 'OwnFleet', providerName: null, transportReference: null, plannedDispatchDate: null,
    source: loc('store', 'Main Store'), destination: loc('van', 'Van 1'), destinationBranch: { id: 'b2', code: 'B2', name: 'Branch 2' },
    leg: { vehicleNumber: 'KA-01', vehicleName: 'Tempo', driverName: 'Ravi', driverPhone: '999' }, loadedAt: null,
    lines: [{ id: 'l1', transferNo: 'TRF-1', productId: 'p', productSku: 'PEN', productName: 'Pen', baseUnitCode: 'pc', productUnitId: 'box', unitCode: 'box', enteredQty: 5, allocatedBaseQty: 50, loadedBaseQty: 20, loadedQty: 2 }],
    ...over,
  };
}

function render(d: ConsignmentDetail) {
  TestBed.configureTestingModule({
    imports: [ConsignmentDetailPage],
    providers: [
      provideRouter([]),
      { provide: ConsignmentsService, useValue: { get: () => of(d) } },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'c1' } } } },
    ],
  });
  const fixture = TestBed.createComponent(ConsignmentDetailPage);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('ConsignmentDetailPage', () => {
  it('incoming: shows only this consignment and its own leg, with no shipment link', () => {
    const el = render(detail());
    expect(el.querySelector('[data-testid="shipment-link"]')).toBeNull();
    expect(el.querySelector('[data-testid="shipment-no"]')?.textContent).toContain('SHP-0001');
    expect(el.querySelector('[data-testid="leg"]')?.textContent).toContain('KA-01');
    expect(el.querySelectorAll('[data-testid="line"]')).toHaveLength(1);
    expect(el.textContent).not.toContain('Cost');
  });

  it('outgoing: links to the shipment planner', () => {
    const el = render(detail({ direction: 'outgoing', shipmentId: 's1' }));
    expect(el.querySelector('[data-testid="shipment-link"]')?.getAttribute('href')).toBe('/inventory/shipments/s1');
  });
});
