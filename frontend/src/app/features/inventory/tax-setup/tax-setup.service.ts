import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type {
  AddTaxVersionRequest,
  CreateTaxProfileRequest,
  ResolveQuery,
  TaxAssignment,
  TaxAssignmentTree,
  TaxComponent,
  TaxProfile,
  TaxProfileDetail,
  TaxResolution,
  TaxSeedResult,
  TaxVersion,
  UpdateTaxProfileRequest,
  UpsertTaxAssignmentRequest,
  UpsertTaxComponentRequest,
} from './tax-setup.models';

const API = environment.apiBaseUrl;
const COMPONENTS = `${API}/tax-components`;
const PROFILES = `${API}/tax-profiles`;
const ASSIGNMENTS = `${API}/tax-assignments`;

@Injectable({ providedIn: 'root' })
export class TaxSetupService {
  private readonly http = inject(HttpClient);

  // ---- components
  listComponents(countryId?: string | null, active?: boolean | null): Observable<TaxComponent[]> {
    return this.http.get<TaxComponent[]>(COMPONENTS, { params: filterParams(countryId, active) });
  }
  createComponent(r: UpsertTaxComponentRequest): Observable<TaxComponent> {
    return this.http.post<TaxComponent>(COMPONENTS, r);
  }
  updateComponent(id: string, r: UpsertTaxComponentRequest): Observable<TaxComponent> {
    return this.http.put<TaxComponent>(`${COMPONENTS}/${id}`, r);
  }
  setComponentActive(id: string, isActive: boolean): Observable<TaxComponent> {
    return this.http.post<TaxComponent>(`${COMPONENTS}/${id}/status`, { isActive });
  }

  // ---- profiles
  listProfiles(countryId?: string | null, active?: boolean | null): Observable<TaxProfile[]> {
    return this.http.get<TaxProfile[]>(PROFILES, { params: filterParams(countryId, active) });
  }
  getProfile(id: string): Observable<TaxProfileDetail> {
    return this.http.get<TaxProfileDetail>(`${PROFILES}/${id}`);
  }
  createProfile(r: CreateTaxProfileRequest): Observable<TaxProfileDetail> {
    return this.http.post<TaxProfileDetail>(PROFILES, r);
  }
  updateProfile(id: string, r: UpdateTaxProfileRequest): Observable<TaxProfileDetail> {
    return this.http.put<TaxProfileDetail>(`${PROFILES}/${id}`, r);
  }
  addVersion(id: string, r: AddTaxVersionRequest): Observable<TaxVersion> {
    return this.http.post<TaxVersion>(`${PROFILES}/${id}/versions`, r);
  }
  setProfileActive(id: string, isActive: boolean): Observable<TaxProfileDetail> {
    return this.http.post<TaxProfileDetail>(`${PROFILES}/${id}/status`, { isActive });
  }
  seedStandard(): Observable<TaxSeedResult> {
    return this.http.post<TaxSeedResult>(`${PROFILES}/seed-standard`, {});
  }

  // ---- assignments
  tree(countryId?: string | null, includeOverrides = true): Observable<TaxAssignmentTree> {
    let params = new HttpParams().set('includeOverrides', includeOverrides);
    if (countryId) params = params.set('countryId', countryId);
    return this.http.get<TaxAssignmentTree>(ASSIGNMENTS, { params });
  }
  resolve(query: ResolveQuery): Observable<TaxResolution> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query)) {
      if (value) params = params.set(key, value);
    }
    return this.http.get<TaxResolution>(`${ASSIGNMENTS}/resolve`, { params });
  }
  upsertAssignment(r: UpsertTaxAssignmentRequest): Observable<TaxAssignment> {
    return this.http.put<TaxAssignment>(ASSIGNMENTS, r);
  }
  deleteAssignment(id: string): Observable<void> {
    return this.http.delete<void>(`${ASSIGNMENTS}/${id}`);
  }
  clearRedundantOverrides(countryId: string): Observable<{ cleared: number }> {
    return this.http.post<{ cleared: number }>(`${ASSIGNMENTS}/clear-redundant-overrides`, { countryId });
  }
}

function filterParams(countryId?: string | null, active?: boolean | null): HttpParams {
  let params = new HttpParams();
  if (countryId) params = params.set('countryId', countryId);
  if (active !== null && active !== undefined) params = params.set('active', active);
  return params;
}
