import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { AccessibleBranchOption, Branch, CreateBranchRequest, UpdateBranchRequest } from './branches.models';

const BASE_URL = `${environment.apiBaseUrl}/branches`;

@Injectable({ providedIn: 'root' })
export class BranchesService {
  private readonly http = inject(HttpClient);

  list(includeInactive: boolean): Observable<Branch[]> {
    let params = new HttpParams();
    if (includeInactive) params = params.set('includeInactive', true);
    return this.http.get<Branch[]>(BASE_URL, { params });
  }

  get(id: string): Observable<Branch> {
    return this.http.get<Branch>(`${BASE_URL}/${id}`);
  }

  create(request: CreateBranchRequest): Observable<Branch> {
    return this.http.post<Branch>(BASE_URL, request);
  }

  update(id: string, request: UpdateBranchRequest): Observable<Branch> {
    return this.http.put<Branch>(`${BASE_URL}/${id}`, request);
  }

  /** POST /branches/{id}/status { isActive, reason } (D14: reason required, 1-500 chars). */
  setStatus(id: string, isActive: boolean, reason: string): Observable<Branch> {
    return this.http.post<Branch>(`${BASE_URL}/${id}/status`, { isActive, reason });
  }

  /** Branches the caller may work in (no permission needed): options for warehouse/vehicle forms. */
  accessible(): Observable<AccessibleBranchOption[]> {
    return this.http.get<AccessibleBranchOption[]>(`${BASE_URL}/accessible`);
  }
}
