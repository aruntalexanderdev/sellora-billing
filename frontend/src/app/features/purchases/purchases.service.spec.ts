import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { PRIMARY_LOAD } from '../../core/http/request-context';
import { PurchasesService } from './purchases.service';

describe('PurchasesService (branch/location scope)', () => {
  let service: PurchasesService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(PurchasesService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('sends branchId and stockLocationId on the purchase list and the returns list', () => {
    service.list({ branchId: 'b2', stockLocationId: 'loc1' }).subscribe();
    const list = http.expectOne((r) => r.url === `${environment.apiBaseUrl}/purchases`);
    expect(list.request.params.get('branchId')).toBe('b2');
    expect(list.request.params.get('stockLocationId')).toBe('loc1');
    list.flush({ items: [], total: 0, page: 1, pageSize: 20 });

    service.returns({ branchId: 'b2', stockLocationId: 'loc1' }).subscribe();
    const ret = http.expectOne((r) => r.url === `${environment.apiBaseUrl}/purchase-returns`);
    expect(ret.request.params.get('branchId')).toBe('b2');
    expect(ret.request.params.get('stockLocationId')).toBe('loc1');
    ret.flush({ items: [], total: 0, page: 1, pageSize: 20 });
  });

  it('flags the main detail load as primary and sends no location', () => {
    service.get('p1').subscribe();
    const req = http.expectOne((r) => r.url === `${environment.apiBaseUrl}/purchases/p1`);
    expect(req.request.params.has('stockLocationId')).toBe(false);
    expect(req.request.context.get(PRIMARY_LOAD)).toBe(true);
    req.flush({});
  });

  it('re-bases returnable quantities with ?stockLocationId as a secondary (non-primary) load', () => {
    service.get('p1', 'loc-van').subscribe();
    const req = http.expectOne((r) => r.url === `${environment.apiBaseUrl}/purchases/p1`);
    expect(req.request.params.get('stockLocationId')).toBe('loc-van');
    expect(req.request.context.get(PRIMARY_LOAD)).toBe(false);
    req.flush({});
  });

  it('flags the purchase return detail as a primary load', () => {
    service.getReturn('r1').subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/purchase-returns/r1`);
    expect(req.request.context.get(PRIMARY_LOAD)).toBe(true);
    req.flush({});
  });
});
