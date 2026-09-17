import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import type { ProductDetail, ProductListItem, UpsertProductRequest } from './products.models';

const BASE_URL = `${environment.apiBaseUrl}/products`;

@Injectable({ providedIn: 'root' })
export class ProductsService {
  private readonly http = inject(HttpClient);

  list(search?: string): Observable<PagedResult<ProductListItem>> {
    const params: Record<string, string> = { page: '1', pageSize: '100' };
    if (search) params['search'] = search;
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

  setActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/status`, { isActive });
  }
}
