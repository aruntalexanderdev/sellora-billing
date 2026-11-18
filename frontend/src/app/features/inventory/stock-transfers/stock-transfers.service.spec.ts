import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { StockTransfersService, readyForShipmentParams, stockTransferListParams } from './stock-transfers.service';

describe('stockTransferListParams', () => {
  it('sends paging and only the filters that are set', () => {
    expect(stockTransferListParams({})).toEqual({ page: '1', pageSize: '20' });
    expect(stockTransferListParams({ status: 'Dispatched', direction: 'incoming', sourceLocationId: 's', destinationLocationId: 'd', dateFrom: '2026-11-01', dateTo: '2026-11-30', page: 3, pageSize: 50 })).toEqual({
      page: '3', pageSize: '50', status: 'Dispatched', direction: 'incoming', sourceLocationId: 's', destinationLocationId: 'd', dateFrom: '2026-11-01', dateTo: '2026-11-30',
    });
  });
});

describe('StockTransfersService', () => {
  function setup() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    return { service: TestBed.inject(StockTransfersService), http: TestBed.inject(HttpTestingController) };
  }

  it('create sends the Idempotency-Key header', () => {
    const { service, http } = setup();
    service.create({ sourceLocationId: 's', destinationLocationId: 'd', notes: null, submit: true, lines: [] }, 'key-1').subscribe();
    const req = http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/stock-transfers'));
    expect(req.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect((req.request.body as { submit: boolean }).submit).toBe(true);
  });

  it('uses PUT for a draft edit and the action routes for the rest', () => {
    const { service, http } = setup();
    service.update('t1', { sourceLocationId: 's', destinationLocationId: 'd', notes: null, lines: [] }).subscribe();
    expect(http.expectOne((r) => r.method === 'PUT' && r.url.endsWith('/stock-transfers/t1')).request.body).not.toHaveProperty('submit');

    service.reject('t1', 'no', 'k').subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/t1/reject')).request.body).toEqual({ reason: 'no' });
    service.receive('t1', { notes: null, lines: [] }, 'k3').subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/t1/receipts')).request.headers.get('Idempotency-Key')).toBe('k3');
    service.resolve('t1', { reason: 'r', lines: [] }, 'k4').subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/t1/resolve')).request.headers.get('Idempotency-Key')).toBe('k4');
  });
});

describe('Phase 03 service calls', () => {
  it('readyForShipmentParams sends paging, filters and scope only when the Company view is on', () => {
    expect(readyForShipmentParams({}, false)).toEqual({ page: '1', pageSize: '20' });
    expect(readyForShipmentParams({ sourceLocationId: 's', search: 'TRF', page: 2, pageSize: 10 }, true)).toEqual({ page: '2', pageSize: '10', sourceLocationId: 's', search: 'TRF', scope: 'company' });
  });

  it('close, ready-for-shipment and history hit the contract routes', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const service = TestBed.inject(StockTransfersService);
    const http = TestBed.inject(HttpTestingController);
    service.close('t1', 'why', 'k').subscribe();
    const close = http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/stock-transfers/t1/close'));
    expect(close.request.body).toEqual({ reason: 'why' });
    service.readyForShipment({ sourceLocationId: 's' }).subscribe();
    const q = http.expectOne((r) => r.url.endsWith('/stock-transfers/ready-for-shipment'));
    expect(q.request.params.get('sourceLocationId')).toBe('s');
    service.history('t1').subscribe();
    http.expectOne((r) => r.method === 'GET' && r.url.endsWith('/stock-transfers/t1/history'));
  });
});
