import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import type {
  ProductDetail,
  ProductListItem,
  UnitPreviewRequest,
  UnitPreviewResponse,
  UpsertProductRequest,
} from './products.models';

const BASE_URL = `${environment.apiBaseUrl}/products`;

export interface ProductListQuery {
  search?: string;
  categoryId?: string | null;
  subcategoryId?: string | null;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class ProductsService {
  private readonly http = inject(HttpClient);

  list(query: ProductListQuery = {}): Observable<PagedResult<ProductListItem>> {
    const params: Record<string, string> = { page: String(query.page ?? 1), pageSize: String(query.pageSize ?? 100) };
    if (query.search) params['search'] = query.search;
    if (query.categoryId) params['categoryId'] = query.categoryId;
    if (query.subcategoryId) params['subcategoryId'] = query.subcategoryId;
    return this.http.get<PagedResult<ProductListItem>>(BASE_URL, { params });
  }

  get(id: string): Observable<ProductDetail> {
    return this.http.get<ProductDetail>(`${BASE_URL}/${id}`);
  }

  create(request: UpsertProductRequest): Observable<ProductDetail> {
    return this.http.post<ProductDetail>(BASE_URL, request);
  }

  update(id: string, request: UpsertProductRequest): Observable<ProductDetail> {
    return this.http.put<ProductDetail>(`${BASE_URL}/${id}`, request);
  }

  /** Validates a unit hierarchy without saving (stateless; 200 even when invalid). */
  previewUnits(request: UnitPreviewRequest): Observable<UnitPreviewResponse> {
    return this.http.post<UnitPreviewResponse>(`${BASE_URL}/unit-preview`, request);
  }

  /** Hard delete; the API refuses (400, field `Product`) once the product has any history. */
  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${BASE_URL}/${id}`);
  }

  setActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/status`, { isActive });
  }
}
