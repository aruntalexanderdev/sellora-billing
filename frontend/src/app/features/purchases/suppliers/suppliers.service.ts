import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import type { SupplierItem, UpsertSupplierRequest } from './suppliers.models';

const BASE_URL = `${environment.apiBaseUrl}/suppliers`;

@Injectable({ providedIn: 'root' })
export class SuppliersService {
  private readonly http = inject(HttpClient);

  list(search?: string): Observable<PagedResult<SupplierItem>> {
    const params: Record<string, string> = { page: '1', pageSize: '100' };
    if (search) params['search'] = search;
    return this.http.get<PagedResult<SupplierItem>>(BASE_URL, { params });
  }

  get(id: string): Observable<SupplierItem> {
    return this.http.get<SupplierItem>(`${BASE_URL}/${id}`);
  }

  create(request: UpsertSupplierRequest): Observable<SupplierItem> {
    return this.http.post<SupplierItem>(BASE_URL, request);
  }

  update(id: string, request: UpsertSupplierRequest): Observable<SupplierItem> {
    return this.http.put<SupplierItem>(`${BASE_URL}/${id}`, request);
  }

  setActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/status`, { isActive });
  }
}
