import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import type { CustomerItem, UpsertCustomerRequest } from './customers.models';

const BASE_URL = `${environment.apiBaseUrl}/customers`;

@Injectable({ providedIn: 'root' })
export class CustomersService {
  private readonly http = inject(HttpClient);

  list(search?: string): Observable<PagedResult<CustomerItem>> {
    const params: Record<string, string> = { page: '1', pageSize: '100' };
    if (search) params['search'] = search;
    return this.http.get<PagedResult<CustomerItem>>(BASE_URL, { params });
  }

  get(id: string): Observable<CustomerItem> {
    return this.http.get<CustomerItem>(`${BASE_URL}/${id}`);
  }

  create(request: UpsertCustomerRequest): Observable<CustomerItem> {
    return this.http.post<CustomerItem>(BASE_URL, request);
  }

  update(id: string, request: UpsertCustomerRequest): Observable<CustomerItem> {
    return this.http.put<CustomerItem>(`${BASE_URL}/${id}`, request);
  }

  setActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/status`, { isActive });
  }
}
