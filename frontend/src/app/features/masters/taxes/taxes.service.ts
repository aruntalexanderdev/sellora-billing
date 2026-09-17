import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { TaxItem, UpsertTaxRequest } from './taxes.models';

const BASE_URL = `${environment.apiBaseUrl}/taxes`;

@Injectable({ providedIn: 'root' })
export class TaxesService {
  private readonly http = inject(HttpClient);

  list(): Observable<TaxItem[]> {
    return this.http.get<TaxItem[]>(BASE_URL);
  }

  create(request: UpsertTaxRequest): Observable<TaxItem> {
    return this.http.post<TaxItem>(BASE_URL, request);
  }

  update(id: string, request: UpsertTaxRequest): Observable<TaxItem> {
    return this.http.put<TaxItem>(`${BASE_URL}/${id}`, request);
  }

  setActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/status`, { isActive });
  }
}
