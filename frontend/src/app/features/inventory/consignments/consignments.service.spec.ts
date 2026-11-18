import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ContextService } from '../../../core/context/context.service';
import { ConsignmentsService } from './consignments.service';

describe('ConsignmentsService (Phase 06)', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: ContextService, useValue: { companyViewActive: () => false } }],
    });
    return { service: TestBed.inject(ConsignmentsService), http: TestBed.inject(HttpTestingController) };
  }

  it('approve posts with no body and the Idempotency-Key', () => {
    const { service, http } = setup();
    service.approveReceipt('c1', 'r1', 'k1').subscribe();
    const req = http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/consignments/c1/receipts/r1/approve'));
    expect(req.request.body).toBeNull();
    expect(req.request.headers.get('Idempotency-Key')).toBe('k1');
  });

  it('reject posts { reason } with the Idempotency-Key', () => {
    const { service, http } = setup();
    service.rejectReceipt('c1', 'r1', 'miscount', 'k2').subscribe();
    const req = http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/consignments/c1/receipts/r1/reject'));
    expect(req.request.body).toEqual({ reason: 'miscount' });
    expect(req.request.headers.get('Idempotency-Key')).toBe('k2');
  });

  it('records a disposition with the Idempotency-Key and lists them', () => {
    const { service, http } = setup();
    const body = { reason: 'broken', lines: [{ consignmentLineId: 'l1', bucket: 'Damaged' as const, action: 'WriteOff' as const, quantity: 2 }] };
    service.recordDisposition('c1', body, 'k3').subscribe();
    const post = http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/consignments/c1/dispositions'));
    expect(post.request.body).toEqual(body);
    expect(post.request.headers.get('Idempotency-Key')).toBe('k3');

    service.dispositions('c1').subscribe();
    expect(http.expectOne((r) => r.method === 'GET' && r.url.endsWith('/consignments/c1/dispositions')).request.method).toBe('GET');
  });
});
