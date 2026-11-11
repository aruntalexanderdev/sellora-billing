import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PRIMARY_LOAD } from '../../../core/http/request-context';
import { StockService } from './stock.service';

describe('StockService', () => {
  function setup() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    return { service: TestBed.inject(StockService), http: TestBed.inject(HttpTestingController) };
  }

  it('sends stockLocationId on overview and detail only when set', () => {
    const { service, http } = setup();
    service.overview({ stockLocationId: 'loc-1' }).subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/stock/overview')).request.params.get('stockLocationId')).toBe('loc-1');
    service.overview({}).subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/stock/overview')).request.params.has('stockLocationId')).toBe(false);
    service.detail('p1', 'loc-2').subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/stock/p1')).request.params.get('stockLocationId')).toBe('loc-2');
  });

  it('marks the detail load as primary only when asked', () => {
    const { service, http } = setup();
    service.detail('p1', null, true).subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/stock/p1')).request.context.get(PRIMARY_LOAD)).toBe(true);
    service.detail('p1').subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/stock/p1')).request.context.get(PRIMARY_LOAD)).toBe(false);
  });
});
