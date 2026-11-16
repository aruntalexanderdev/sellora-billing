import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { ContextService } from '../../core/context/context.service';
import { ReportsService } from './reports.service';

describe('ReportsService (branch/location scope)', () => {
  let service: ReportsService;
  let http: HttpTestingController;
  const url = (path: string) => `${environment.apiBaseUrl}/reports/${path}`;
  const page = { items: [], total: 0, page: 1, pageSize: 20 };

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(ReportsService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('sends stockLocationId on current stock and stock movement', () => {
    service.currentStock({ stockLocationId: 'loc1' }).subscribe();
    const a = http.expectOne((r) => r.url === url('current-stock'));
    expect(a.request.params.get('stockLocationId')).toBe('loc1');
    expect(a.request.params.has('branchId')).toBe(false);
    a.flush(page);

    service.stockMovement({ stockLocationId: 'loc2' }).subscribe();
    const b = http.expectOne((r) => r.url === url('stock-movement'));
    expect(b.request.params.get('stockLocationId')).toBe('loc2');
    b.flush(page);
  });

  it('sends branchId on purchase, sales and profit reports', () => {
    service.purchases({ branchId: 'b2' }).subscribe();
    const p = http.expectOne((r) => r.url === url('purchases'));
    expect(p.request.params.get('branchId')).toBe('b2');
    p.flush(page);

    service.sales({ branchId: 'b2' }).subscribe();
    const s = http.expectOne((r) => r.url === url('sales'));
    expect(s.request.params.get('branchId')).toBe('b2');
    s.flush(page);

    service.profitSummary({ branchId: 'b2' }).subscribe();
    const f = http.expectOne((r) => r.url === url('profit-summary'));
    expect(f.request.params.get('branchId')).toBe('b2');
    f.flush({});
  });

  it('omits empty scope filters', () => {
    service.sales({ branchId: null }).subscribe();
    const s = http.expectOne((r) => r.url === url('sales'));
    expect(s.request.params.has('branchId')).toBe(false);
    s.flush(page);
  });

  it('adds scope=company only when the Company view is on and the page permission is held company-level (FI-D11)', () => {
    const ctx = TestBed.inject(ContextService);
    ctx.setCompanyView(true);
    service.sales({}).subscribe(); // no companyPermissions loaded: the view cannot be active
    const off = http.expectOne((r) => r.url === url('sales'));
    expect(off.request.params.has('scope')).toBe(false);
    off.flush(page);

    vi.spyOn(ctx, 'companyViewActive').mockImplementation((code) => code === 'Reports.Sales');
    service.sales({}).subscribe();
    const on = http.expectOne((r) => r.url === url('sales'));
    expect(on.request.params.get('scope')).toBe('company');
    on.flush(page);

    service.purchases({}).subscribe(); // a different permission is not held company-level
    const other = http.expectOne((r) => r.url === url('purchases'));
    expect(other.request.params.has('scope')).toBe(false);
    other.flush(page);
  });
});
