import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { CurrencyItem, UpsertCurrencyRequest } from './currencies.models';

const BASE_URL = `${environment.apiBaseUrl}/currencies`;

@Injectable({ providedIn: 'root' })
export class CurrenciesService {
  private readonly http = inject(HttpClient);

  list(): Observable<CurrencyItem[]> {
    return this.http.get<CurrencyItem[]>(BASE_URL);
  }

  create(request: UpsertCurrencyRequest): Observable<CurrencyItem> {
    return this.http.post<CurrencyItem>(BASE_URL, request);
  }

  update(id: string, request: UpsertCurrencyRequest): Observable<CurrencyItem> {
    return this.http.put<CurrencyItem>(`${BASE_URL}/${id}`, request);
  }

  setActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/status`, { isActive });
  }
}
