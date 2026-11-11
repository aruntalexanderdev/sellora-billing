import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { StockLocation, StockLocationQuery } from './locations.models';

const BASE_URL = `${environment.apiBaseUrl}/stock-locations`;

/** GET /stock-locations (policy StockLocations.View). Read-only; the list is ordered by the server (default store first). */
@Injectable({ providedIn: 'root' })
export class StockLocationsService {
  private readonly http = inject(HttpClient);

  list(query: StockLocationQuery = {}): Observable<StockLocation[]> {
    let params = new HttpParams();
    if (query.type) params = params.set('type', query.type);
    if (query.branchId) params = params.set('branchId', query.branchId);
    if (query.includeInactive) params = params.set('includeInactive', true);
    return this.http.get<StockLocation[]>(BASE_URL, { params });
  }

  get(id: string): Observable<StockLocation> {
    return this.http.get<StockLocation>(`${BASE_URL}/${id}`);
  }
}
