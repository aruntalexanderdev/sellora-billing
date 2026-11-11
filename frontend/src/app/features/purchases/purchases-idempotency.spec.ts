import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { SalesService } from '../sales/sales.service';
import { PurchasesService } from './purchases.service';

describe('document-creating requests carry Idempotency-Key and location params', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }));

  it('purchase return POST sends the key', () => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.inject(PurchasesService).createReturn({ purchaseOrderId: 'p', returnDate: '2026-11-01', lines: [] }, 'key-1').subscribe();
    expect(http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/purchase-returns')).request.headers.get('Idempotency-Key')).toBe('key-1');
  });

  it('sales return POST sends the key', () => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.inject(SalesService).createReturn({ salesInvoiceId: 's', returnDate: '2026-11-01', lines: [] }, 'key-2').subscribe();
    expect(http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/sales-returns')).request.headers.get('Idempotency-Key')).toBe('key-2');
  });

  it('catalog and barcode lookups pass stockLocationId only when set', () => {
    const http = TestBed.inject(HttpTestingController);
    const sales = TestBed.inject(SalesService);
    sales.catalog(undefined, 'van').subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/sales/catalog')).request.params.get('stockLocationId')).toBe('van');
    sales.catalog().subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/sales/catalog')).request.params.has('stockLocationId')).toBe(false);
    sales.lookupBarcode('123', 'van').subscribe();
    expect(http.expectOne((r) => r.url.includes('/catalog/by-barcode/123')).request.params.get('stockLocationId')).toBe('van');
  });
});
