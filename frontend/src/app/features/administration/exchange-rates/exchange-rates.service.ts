import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { ExchangeRateItem, ResolvedExchangeRate, UpsertExchangeRateRequest } from './exchange-rates.models';

const BASE_URL = `${environment.apiBaseUrl}/exchange-rates`;

@Injectable({ providedIn: 'root' })
export class ExchangeRatesService {
  private readonly http = inject(HttpClient);

  list(currencyId?: string): Observable<ExchangeRateItem[]> {
    let params = new HttpParams();
    if (currencyId) params = params.set('currencyId', currencyId);
    return this.http.get<ExchangeRateItem[]>(BASE_URL, { params });
  }

  get(id: string): Observable<ExchangeRateItem> {
    return this.http.get<ExchangeRateItem>(`${BASE_URL}/${id}`);
  }

  create(request: UpsertExchangeRateRequest): Observable<ExchangeRateItem> {
    return this.http.post<ExchangeRateItem>(BASE_URL, request);
  }

  update(id: string, request: UpsertExchangeRateRequest): Observable<ExchangeRateItem> {
    return this.http.put<ExchangeRateItem>(`${BASE_URL}/${id}`, request);
  }

  resolve(currencyId: string, date: string): Observable<ResolvedExchangeRate> {
    const params = new HttpParams().set('currencyId', currencyId).set('date', date);
    return this.http.get<ResolvedExchangeRate>(`${BASE_URL}/resolve`, { params });
  }
}
