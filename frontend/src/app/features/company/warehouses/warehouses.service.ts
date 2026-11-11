import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { CreateWarehouseRequest, UpdateWarehouseRequest, Warehouse, WarehouseQuery } from './warehouses.models';

const BASE_URL = `${environment.apiBaseUrl}/warehouses`;

@Injectable({ providedIn: 'root' })
export class WarehousesService {
  private readonly http = inject(HttpClient);

  list(query: WarehouseQuery = {}): Observable<Warehouse[]> {
    let params = new HttpParams();
    if (query.branchId) params = params.set('branchId', query.branchId);
    if (query.ownership) params = params.set('ownership', query.ownership);
    if (query.includeInactive) params = params.set('includeInactive', true);
    return this.http.get<Warehouse[]>(BASE_URL, { params });
  }

  get(id: string): Observable<Warehouse> {
    return this.http.get<Warehouse>(`${BASE_URL}/${id}`);
  }

  create(request: CreateWarehouseRequest): Observable<Warehouse> {
    return this.http.post<Warehouse>(BASE_URL, request);
  }

  update(id: string, request: UpdateWarehouseRequest): Observable<Warehouse> {
    return this.http.put<Warehouse>(`${BASE_URL}/${id}`, request);
  }

  setStatus(id: string, isActive: boolean, reason: string): Observable<Warehouse> {
    return this.http.post<Warehouse>(`${BASE_URL}/${id}/status`, { isActive, reason });
  }
}
