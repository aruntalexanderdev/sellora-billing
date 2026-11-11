import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { PRIMARY_LOAD } from '../../core/http/request-context';
import { SalesService } from './sales.service';

describe('SalesService (branch/location scope)', () => {
  let service: SalesService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(SalesService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('sends branchId and stockLocationId on the invoice list, omitting empty values', () => {
    service.list({ branchId: 'b2', stockLocationId: 'loc1', search: 'INV' }).subscribe();
    const req = http.expectOne((r) => r.url === `${environment.apiBaseUrl}/sales`);
    expect(req.request.params.get('branchId')).toBe('b2');
    expect(req.request.params.get('stockLocationId')).toBe('loc1');
    expect(req.request.context.get(PRIMARY_LOAD)).toBe(false);
    req.flush({ items: [], total: 0, page: 1, pageSize: 20 });

    service.list({ branchId: null, stockLocationId: null }).subscribe();
    const bare = http.expectOne((r) => r.url === `${environment.apiBaseUrl}/sales`);
    expect(bare.request.params.has('branchId')).toBe(false);
    expect(bare.request.params.has('stockLocationId')).toBe(false);
    bare.flush({ items: [], total: 0, page: 1, pageSize: 20 });
  });

  it('sends branchId and stockLocationId on the returns list', () => {
    service.returns({ branchId: 'b2', stockLocationId: 'loc1' }).subscribe();
    const req = http.expectOne((r) => r.url === `${environment.apiBaseUrl}/sales-returns`);
    expect(req.request.params.get('branchId')).toBe('b2');
    expect(req.request.params.get('stockLocationId')).toBe('loc1');
    req.flush({ items: [], total: 0, page: 1, pageSize: 20 });
  });

  it('flags the invoice and return detail loads as primary loads', () => {
    service.get('s1').subscribe();
    const inv = http.expectOne(`${environment.apiBaseUrl}/sales/s1`);
    expect(inv.request.context.get(PRIMARY_LOAD)).toBe(true);
    inv.flush({});

    service.getReturn('r1').subscribe();
    const ret = http.expectOne(`${environment.apiBaseUrl}/sales-returns/r1`);
    expect(ret.request.context.get(PRIMARY_LOAD)).toBe(true);
    ret.flush({});
  });
});
