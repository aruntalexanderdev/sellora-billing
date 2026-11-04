import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { UnitItem, UpsertUnitRequest } from './units.models';

const BASE_URL = `${environment.apiBaseUrl}/units`;

@Injectable({ providedIn: 'root' })
export class UnitsService {
  private readonly http = inject(HttpClient);

  list(): Observable<UnitItem[]> {
    return this.http.get<UnitItem[]>(BASE_URL);
  }

  get(id: string): Observable<UnitItem> {
    return this.http.get<UnitItem>(`${BASE_URL}/${id}`);
  }

  create(request: UpsertUnitRequest): Observable<UnitItem> {
    return this.http.post<UnitItem>(BASE_URL, request);
  }

  update(id: string, request: UpsertUnitRequest): Observable<UnitItem> {
    return this.http.put<UnitItem>(`${BASE_URL}/${id}`, request);
  }

  setActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/status`, { isActive });
  }
}
