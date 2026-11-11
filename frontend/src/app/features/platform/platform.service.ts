import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type {
  CreatePlatformCompanyRequest, CreatedPlatformCompany, CurrencyItem, PlatformCompany, PlatformCurrencyOption,
  SetPlatformCompanyStatusRequest, UnitItem, UpsertCurrencyRequest, UpsertUnitRequest,
} from './platform.models';

const BASE_URL = `${environment.apiBaseUrl}/platform`;

/** Platform console API. The platform token is attached by core/platform/platform.interceptor.ts (these are /platform/* paths). */
@Injectable({ providedIn: 'root' })
export class PlatformService {
  private readonly http = inject(HttpClient);

  companies(): Observable<PlatformCompany[]> {
    return this.http.get<PlatformCompany[]>(`${BASE_URL}/companies`);
  }

  createCompany(request: CreatePlatformCompanyRequest): Observable<CreatedPlatformCompany> {
    return this.http.post<CreatedPlatformCompany>(`${BASE_URL}/companies`, request);
  }

  setCompanyStatus(id: string, request: SetPlatformCompanyStatusRequest): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/companies/${id}/status`, request);
  }

  activeCurrencies(): Observable<PlatformCurrencyOption[]> {
    return this.http.get<PlatformCurrencyOption[]>(`${BASE_URL}/currencies`);
  }

  units(): Observable<UnitItem[]> {
    return this.http.get<UnitItem[]>(`${BASE_URL}/units`);
  }

  createUnit(request: UpsertUnitRequest): Observable<UnitItem> {
    return this.http.post<UnitItem>(`${BASE_URL}/units`, request);
  }

  updateUnit(id: string, request: UpsertUnitRequest): Observable<UnitItem> {
    return this.http.put<UnitItem>(`${BASE_URL}/units/${id}`, request);
  }

  setUnitActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/units/${id}/status`, { isActive });
  }

  currencies(): Observable<CurrencyItem[]> {
    return this.http.get<CurrencyItem[]>(`${BASE_URL}/currencies/all`);
  }

  createCurrency(request: UpsertCurrencyRequest): Observable<CurrencyItem> {
    return this.http.post<CurrencyItem>(`${BASE_URL}/currencies`, request);
  }

  updateCurrency(id: string, request: UpsertCurrencyRequest): Observable<CurrencyItem> {
    return this.http.put<CurrencyItem>(`${BASE_URL}/currencies/${id}`, request);
  }

  setCurrencyActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/currencies/${id}/status`, { isActive });
  }
}
