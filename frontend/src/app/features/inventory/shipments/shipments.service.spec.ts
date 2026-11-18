import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ContextService } from '../../../core/context/context.service';
import { ConsignmentsService, consignmentListParams } from '../consignments/consignments.service';
import { ReservationsService, reservationListParams } from '../stock-reservations/reservations.service';
import { TransitService, transitParams } from '../transit/transit.service';
import { ShipmentsService, shipmentListParams } from './shipments.service';

describe('list params', () => {
  it('shipments: paging plus only the filters that are set; scope=company only in Company view', () => {
    expect(shipmentListParams({}, false)).toEqual({ page: '1', pageSize: '20' });
    expect(shipmentListParams({ status: 'Loading', mode: 'Air', sourceLocationId: 's', search: 'AWB', page: 2, pageSize: 10 }, true)).toEqual({
      page: '2', pageSize: '10', status: 'Loading', mode: 'Air', sourceLocationId: 's', search: 'AWB', scope: 'company',
    });
    expect(shipmentListParams({ stockTransferId: 't1' }, false)).toEqual({ page: '1', pageSize: '20', stockTransferId: 't1' });
  });

  it('consignments: direction is always sent', () => {
    expect(consignmentListParams({ direction: 'incoming' }, false)).toEqual({ direction: 'incoming', page: '1', pageSize: '20' });
    expect(consignmentListParams({ direction: 'outgoing', status: 'Loaded', shipmentId: 'sh', search: 'x' }, true)).toMatchObject({ direction: 'outgoing', status: 'Loaded', shipmentId: 'sh', search: 'x', scope: 'company' });
  });

  it('reservations: status is omitted unless chosen (server default Active)', () => {
    expect(reservationListParams({}, false)).toEqual({ page: '1', pageSize: '20' });
    expect(reservationListParams({ locationId: 'l', productId: 'p', status: 'Released' }, false)).toMatchObject({ locationId: 'l', productId: 'p', status: 'Released' });
  });
});

describe('ShipmentsService', () => {
  function setup(companyView = false) {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: ContextService, useValue: { companyViewActive: () => companyView } }],
    });
    return { service: TestBed.inject(ShipmentsService), http: TestBed.inject(HttpTestingController) };
  }

  it('list adds scope=company while the Company view is on', () => {
    const { service, http } = setup(true);
    service.list({ status: 'Draft' }).subscribe();
    const req = http.expectOne((r) => r.method === 'GET' && r.url.endsWith('/shipments'));
    expect(req.request.params.get('scope')).toBe('company');
    expect(req.request.params.get('status')).toBe('Draft');
  });

  it('create, cancel, allocate, change allocation and load send the Idempotency-Key', () => {
    const { service, http } = setup();
    service.create({ sourceLocationId: 's', transportMode: 'Air', transportVia: 'ExternalProvider', providerName: 'DHL', providerContact: null, transportReference: 'AWB1', plannedDispatchDate: null, notes: null }, 'k1').subscribe();
    expect(http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/shipments')).request.headers.get('Idempotency-Key')).toBe('k1');

    service.cancel('s1', 'why', 'k2').subscribe();
    const cancel = http.expectOne((r) => r.url.endsWith('/shipments/s1/cancel'));
    expect(cancel.request.headers.get('Idempotency-Key')).toBe('k2');
    expect(cancel.request.body).toEqual({ reason: 'why' });

    service.allocate('s1', { stockTransferLineId: 'l', productUnitId: 'u', quantity: 2 }, 'k3').subscribe();
    expect(http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/shipments/s1/allocations')).request.headers.get('Idempotency-Key')).toBe('k3');

    service.changeAllocation('s1', 'cl1', 0, 'k4').subscribe();
    const change = http.expectOne((r) => r.method === 'PUT' && r.url.endsWith('/shipments/s1/allocations/cl1'));
    expect(change.request.headers.get('Idempotency-Key')).toBe('k4');
    expect(change.request.body).toEqual({ quantity: 0 });

    service.load('s1', 'c1', { lines: [{ consignmentLineId: 'cl1', loadedQuantity: 3 }] }, 'k5').subscribe();
    const load = http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/shipments/s1/consignments/c1/load'));
    expect(load.request.headers.get('Idempotency-Key')).toBe('k5');
    expect(load.request.body).toEqual({ lines: [{ consignmentLineId: 'cl1', loadedQuantity: 3 }] });
  });

  it('the state-idempotent writes use the contract routes without a key', () => {
    const { service, http } = setup();
    service.update('s1', { transportMode: 'Road', transportVia: 'OwnFleet', providerName: null, providerContact: null, transportReference: null, plannedDispatchDate: null, notes: null }).subscribe();
    expect(http.expectOne((r) => r.method === 'PUT' && r.url.endsWith('/shipments/s1')).request.headers.has('Idempotency-Key')).toBe(false);
    service.addVehicle('s1', { vehicleId: 'v', externalVehicleNumber: null, externalDescription: null, driverName: null, driverPhone: null }).subscribe();
    http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/shipments/s1/vehicles'));
    service.removeVehicle('s1', 'sv').subscribe();
    http.expectOne((r) => r.method === 'DELETE' && r.url.endsWith('/shipments/s1/vehicles/sv'));
    service.removeAllocation('s1', 'cl1').subscribe();
    http.expectOne((r) => r.method === 'DELETE' && r.url.endsWith('/shipments/s1/allocations/cl1'));
    service.assignVehicle('s1', 'c1', null).subscribe();
    const leg = http.expectOne((r) => r.method === 'PUT' && r.url.endsWith('/shipments/s1/consignments/c1/vehicle'));
    expect(leg.request.body).toEqual({ shipmentVehicleId: null });
    service.cancelConsignment('s1', 'c1', 'oops').subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/shipments/s1/consignments/c1/cancel')).request.body).toEqual({ reason: 'oops' });
  });
});

describe('ConsignmentsService / ReservationsService', () => {
  it('hit the contract routes', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), { provide: ContextService, useValue: { companyViewActive: () => false } }] });
    const http = TestBed.inject(HttpTestingController);
    TestBed.inject(ConsignmentsService).list({ direction: 'incoming' }).subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/consignments')).request.params.get('direction')).toBe('incoming');
    TestBed.inject(ConsignmentsService).get('c1').subscribe();
    http.expectOne((r) => r.method === 'GET' && r.url.endsWith('/consignments/c1'));
    TestBed.inject(ReservationsService).list({ status: 'Active' }).subscribe();
    http.expectOne((r) => r.method === 'GET' && r.url.endsWith('/stock-reservations'));
    TestBed.inject(ReservationsService).release('r1', 'stale').subscribe();
    const rel = http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/stock-reservations/r1/release'));
    expect(rel.request.body).toEqual({ reason: 'stale' });
  });
});

describe('Phase 05 services', () => {
  function setup(companyView = false) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), { provide: ContextService, useValue: { companyViewActive: () => companyView } }] });
    return TestBed.inject(HttpTestingController);
  }

  it('dispatch POSTs without a body and with the Idempotency-Key', () => {
    const http = setup();
    TestBed.inject(ShipmentsService).dispatch('s1', 'kd').subscribe();
    const req = http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/shipments/s1/dispatch'));
    expect(req.request.headers.get('Idempotency-Key')).toBe('kd');
    expect(req.request.body).toBeNull();
  });

  it('receipts: list, get, record (with key + body) and post (with key)', () => {
    const http = setup();
    const service = TestBed.inject(ConsignmentsService);
    service.receipts('c1').subscribe();
    http.expectOne((r) => r.method === 'GET' && r.url.endsWith('/consignments/c1/receipts'));
    service.receipt('c1', 'r1').subscribe();
    http.expectOne((r) => r.method === 'GET' && r.url.endsWith('/consignments/c1/receipts/r1'));

    const body = { lines: [{ consignmentLineId: 'cl1', acceptedQuantity: 3, damagedQuantity: 1, rejectedQuantity: 0, missingQuantity: 0 }], notes: 'n' };
    service.recordReceipt('c1', body, 'kr').subscribe();
    const rec = http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/consignments/c1/receipts'));
    expect(rec.request.headers.get('Idempotency-Key')).toBe('kr');
    expect(rec.request.body).toEqual(body);

    service.postReceipt('c1', 'r1', 'kp').subscribe();
    const post = http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/consignments/c1/receipts/r1/post'));
    expect(post.request.headers.get('Idempotency-Key')).toBe('kp');
  });

  it('transit sends the view always and scope=company only in Company view', () => {
    expect(transitParams({ view: 'current' }, false)).toEqual({ view: 'current', page: '1', pageSize: '20' });
    expect(transitParams({ view: 'discrepancies', search: 'x', page: 2, pageSize: 10 }, true)).toEqual({ view: 'discrepancies', search: 'x', page: '2', pageSize: '10', scope: 'company' });
    const http = setup(true);
    TestBed.inject(TransitService).list({ view: 'incoming' }).subscribe();
    const req = http.expectOne((r) => r.method === 'GET' && r.url.endsWith('/transit'));
    expect(req.request.params.get('view')).toBe('incoming');
    expect(req.request.params.get('scope')).toBe('company');
  });
});
