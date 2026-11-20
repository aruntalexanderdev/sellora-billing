import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type {
  CountryDto, CountryListDto, CountryLookupDto, CreateCountryRequest, CreateStateRequest, GeoLookup, GeographyApi, StateDto, StateListDto,
  StateLookupDto, UpdateCountryRequest, UpdateStateRequest,
} from './geo.models';

const COUNTRIES = `${environment.apiBaseUrl}/countries`;
const STATES = `${environment.apiBaseUrl}/states`;

/** Company API for countries/states: lookups for the select pair (any signed-in user) and the Masters admin screens. */
@Injectable({ providedIn: 'root' })
export class GeoService implements GeoLookup, GeographyApi {
  private readonly http = inject(HttpClient);

  lookupCountries(): Observable<CountryLookupDto[]> {
    return this.http.get<CountryLookupDto[]>(`${COUNTRIES}/lookup`);
  }

  lookupStates(countryId: string): Observable<StateLookupDto[]> {
    return this.http.get<StateLookupDto[]>(`${STATES}/lookup`, { params: new HttpParams().set('countryId', countryId) });
  }

  listCountries(includeInactive: boolean): Observable<CountryListDto> {
    return this.http.get<CountryListDto>(COUNTRIES, { params: new HttpParams().set('includeInactive', includeInactive) });
  }

  createCountry(r: CreateCountryRequest): Observable<CountryDto> {
    return this.http.post<CountryDto>(COUNTRIES, r);
  }

  updateCountry(id: string, r: UpdateCountryRequest): Observable<CountryDto> {
    return this.http.put<CountryDto>(`${COUNTRIES}/${id}`, r);
  }

  setCountryActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${COUNTRIES}/${id}/status`, { isActive });
  }

  listStates(countryId: string | null, includeInactive: boolean): Observable<StateListDto> {
    let params = new HttpParams().set('includeInactive', includeInactive);
    if (countryId) params = params.set('countryId', countryId);
    return this.http.get<StateListDto>(STATES, { params });
  }

  createState(r: CreateStateRequest): Observable<StateDto> {
    return this.http.post<StateDto>(STATES, r);
  }

  updateState(id: string, r: UpdateStateRequest): Observable<StateDto> {
    return this.http.put<StateDto>(`${STATES}/${id}`, r);
  }

  setStateActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${STATES}/${id}/status`, { isActive });
  }
}
