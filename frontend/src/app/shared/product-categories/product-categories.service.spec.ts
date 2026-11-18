import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ProductCategoriesService } from './product-categories.service';
import { ProductsService } from '../../features/inventory/products/products.service';
import { StockService } from '../../features/inventory/stock/stock.service';
import { ContextService } from '../../core/context/context.service';

describe('classification service params', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: ContextService, useValue: { companyViewActive: () => false } }],
    });
    http = TestBed.inject(HttpTestingController);
  });

  it('list sends includeInactive only when true', () => {
    const s = TestBed.inject(ProductCategoriesService);
    s.list().subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/product-categories')).request.params.has('includeInactive')).toBe(false);
    s.list(true).subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/product-categories')).request.params.get('includeInactive')).toBe('true');
  });

  it('subcategory writes hit nested routes', () => {
    const s = TestBed.inject(ProductCategoriesService);
    s.updateSubcategory('c1', 's1', { code: 'A', name: 'A', sortOrder: 0 }).subscribe();
    expect(http.expectOne((r) => r.url.endsWith('/product-categories/c1/subcategories/s1')).request.method).toBe('PUT');
    s.setSubcategoryActive('c1', 's1', false).subscribe();
    const req = http.expectOne((r) => r.url.endsWith('/product-categories/c1/subcategories/s1/status'));
    expect(req.request.body).toEqual({ isActive: false });
  });

  it('products and stock overview send categoryId/subcategoryId', () => {
    TestBed.inject(ProductsService).list({ categoryId: 'c1', subcategoryId: 's1' }).subscribe();
    const p = http.expectOne((r) => r.url.endsWith('/products')).request.params;
    expect([p.get('categoryId'), p.get('subcategoryId')]).toEqual(['c1', 's1']);
    TestBed.inject(StockService).overview({ categoryId: 'c1' }).subscribe();
    const o = http.expectOne((r) => r.url.endsWith('/stock/overview')).request.params;
    expect(o.get('categoryId')).toBe('c1');
    expect(o.has('subcategoryId')).toBe(false);
  });
});
