import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { CreateRoleRequest, RoleDetail, RoleListItem, UpdateRoleRequest } from './roles.models';

const BASE_URL = `${environment.apiBaseUrl}/roles`;

@Injectable({ providedIn: 'root' })
export class RolesService {
  private readonly http = inject(HttpClient);

  list(): Observable<RoleListItem[]> {
    return this.http.get<RoleListItem[]>(BASE_URL);
  }

  get(id: string): Observable<RoleDetail> {
    return this.http.get<RoleDetail>(`${BASE_URL}/${id}`);
  }

  create(request: CreateRoleRequest): Observable<RoleDetail> {
    return this.http.post<RoleDetail>(BASE_URL, request);
  }

  update(id: string, request: UpdateRoleRequest): Observable<RoleDetail> {
    return this.http.put<RoleDetail>(`${BASE_URL}/${id}`, request);
  }

  duplicate(id: string): Observable<RoleDetail> {
    return this.http.post<RoleDetail>(`${BASE_URL}/${id}/duplicate`, {});
  }

  setActive(id: string, isActive: boolean): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/${id}/status`, { isActive });
  }
}
