import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { CurrencyItem } from './currencies.models';

const BASE_URL = `${environment.apiBaseUrl}/currencies`;

@Injectable({ providedIn: 'root' })
export class CurrenciesService {
  private readonly http = inject(HttpClient);

  list(): Observable<CurrencyItem[]> {
    return this.http.get<CurrencyItem[]>(BASE_URL);
  }

  setActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/status`, { isActive });
  }
}
