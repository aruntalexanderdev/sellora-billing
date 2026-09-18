import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PricingRow, SellingPriceHistoryEntry } from './pricing.models';

const BASE_URL = `${environment.apiBaseUrl}/pricing`;

@Injectable({ providedIn: 'root' })
export class PricingService {
  private readonly http = inject(HttpClient);

  list(search?: string): Observable<PricingRow[]> {
    const params: Record<string, string> = {};
    if (search) params['search'] = search;
    return this.http.get<PricingRow[]>(BASE_URL, { params });
  }

  history(sellingModeId: string): Observable<SellingPriceHistoryEntry[]> {
    return this.http.get<SellingPriceHistoryEntry[]>(`${BASE_URL}/${sellingModeId}/history`);
  }

  setPrice(sellingModeId: string, price: number): Observable<void> {
    return this.http.put<void>(`${BASE_URL}/${sellingModeId}`, { price });
  }
}
