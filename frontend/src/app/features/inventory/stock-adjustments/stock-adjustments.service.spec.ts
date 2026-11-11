import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../../environments/environment';
import { PRIMARY_LOAD } from '../../../core/http/request-context';
import { StockAdjustmentsService, stockAdjustmentListParams } from './stock-adjustments.service';

describe('StockAdjustmentsService (location scope)', () => {
  it('maps branchId and stockLocationId to query params', () => {
    expect(stockAdjustmentListParams({ branchId: 'b2', stockLocationId: 'loc1' })).toEqual({ page: '1', pageSize: '20', branchId: 'b2', stockLocationId: 'loc1' });
    expect(stockAdjustmentListParams({ branchId: null, stockLocationId: null })).toEqual({ page: '1', pageSize: '20' });
  });

  it('flags the detail load as a primary load', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    TestBed.inject(StockAdjustmentsService).get('a1').subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/stock-adjustments/a1`);
    expect(req.request.context.get(PRIMARY_LOAD)).toBe(true);
    req.flush({});
    http.verify();
  });
});
