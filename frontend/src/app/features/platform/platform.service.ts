import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../../environments/environment';
import type {
  CountryDto, CountryListDto, CountryLookupDto, CreateCountryRequest, CreateStateRequest, GeoLookup, GeographyApi, StateDto, StateListDto, StateLookupDto,
  UpdateCountryRequest, UpdateStateRequest,
} from '../../shared/geo/geo.models';
import type {
  CreatePlatformCompanyRequest, CreatedPlatformCompany, CurrencyItem, DataResetOptions, DataResetReport, DataResetStatus, DbReleaseRetryResponse, DbReleaseRunDetailDto, DbReleasesOverviewDto, DbScriptDetailDto, PlatformCompany, PlatformCurrencyOption,
  RetryDbReleaseRequest, SetPlatformCompanyStatusRequest, UnitItem, UpsertCurrencyRequest, UpsertUnitRequest,
} from './platform.models';

const BASE_URL = `${environment.apiBaseUrl}/platform`;

/** Platform console API. The platform token is attached by core/platform/platform.interceptor.ts (these are /platform/* paths). */
@Injectable({ providedIn: 'root' })
export class PlatformService implements GeographyApi, GeoLookup {
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

  // ---- Countries / states (country-state-masters; platform callers are always writable, no canEdit wrapper) ----

  lookupCountries(): Observable<CountryLookupDto[]> {
    return this.http.get<CountryLookupDto[]>(`${BASE_URL}/countries`);
  }

  lookupStates(countryId: string): Observable<StateLookupDto[]> {
    return this.http.get<StateLookupDto[]>(`${BASE_URL}/states`, { params: new HttpParams().set('countryId', countryId) });
  }

  listCountries(includeInactive: boolean): Observable<CountryListDto> {
    return this.http.get<CountryDto[]>(`${BASE_URL}/countries/all`).pipe(map((items) => ({ canEdit: true, items: includeInactive ? items : items.filter((c) => c.isActive) })));
  }

  createCountry(r: CreateCountryRequest): Observable<CountryDto> {
    return this.http.post<CountryDto>(`${BASE_URL}/countries`, r);
  }

  updateCountry(id: string, r: UpdateCountryRequest): Observable<CountryDto> {
    return this.http.put<CountryDto>(`${BASE_URL}/countries/${id}`, r);
  }

  setCountryActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/countries/${id}/status`, { isActive });
  }

  listStates(countryId: string | null, includeInactive: boolean): Observable<StateListDto> {
    const params = countryId ? new HttpParams().set('countryId', countryId) : undefined;
    return this.http.get<StateDto[]>(`${BASE_URL}/states/all`, { params }).pipe(map((items) => ({ canEdit: true, items: includeInactive ? items : items.filter((s) => s.isActive) })));
  }

  createState(r: CreateStateRequest): Observable<StateDto> {
    return this.http.post<StateDto>(`${BASE_URL}/states`, r);
  }

  updateState(id: string, r: UpdateStateRequest): Observable<StateDto> {
    return this.http.put<StateDto>(`${BASE_URL}/states/${id}`, r);
  }

  setStateActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/states/${id}/status`, { isActive });
  }

  dbReleases(runs?: number): Observable<DbReleasesOverviewDto> {
    const params = runs ? { runs: String(runs) } : undefined;
    return this.http.get<DbReleasesOverviewDto>(`${BASE_URL}/db-releases`, { params });
  }

  retryDbRelease(request: RetryDbReleaseRequest): Observable<DbReleaseRetryResponse> {
    return this.http.post<DbReleaseRetryResponse>(`${BASE_URL}/db-releases/retry`, request);
  }

  dbReleaseRun(id: string): Observable<DbReleaseRunDetailDto> {
    return this.http.get<DbReleaseRunDetailDto>(`${BASE_URL}/db-releases/runs/${id}`);
  }

  dbScript(id: string): Observable<DbScriptDetailDto> {
    return this.http.get<DbScriptDetailDto>(`${BASE_URL}/db-releases/scripts/${id}`);
  }

  dataResetStatus(): Observable<DataResetStatus> {
    return this.http.get<DataResetStatus>(`${BASE_URL}/data-reset/status`);
  }

  dataResetPreview(options: DataResetOptions): Observable<DataResetReport> {
    const params = {
      wipeCustomers: String(options.wipeCustomers),
      wipeSuppliers: String(options.wipeSuppliers),
      keepTaxes: String(options.keepTaxes),
      keepCategories: String(options.keepCategories),
      keepExpenseCategories: String(options.keepExpenseCategories),
      keepAccounts: String(options.keepAccounts),
    };
    return this.http.get<DataResetReport>(`${BASE_URL}/data-reset/preview`, { params });
  }

  dataReset(confirmDatabase: string, options: DataResetOptions): Observable<DataResetReport> {
    return this.http.post<DataResetReport>(`${BASE_URL}/data-reset`, { confirmDatabase, ...options });
  }
}
