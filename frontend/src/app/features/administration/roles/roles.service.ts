import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { BranchMember, CreateRoleRequest, RoleDetail, RoleListFilter, RoleListItem, UpdateRoleRequest } from './roles.models';

const BASE_URL = `${environment.apiBaseUrl}/roles`;

@Injectable({ providedIn: 'root' })
export class RolesService {
  private readonly http = inject(HttpClient);

  /** Company roles by default; `scope=Branch` (optionally with `branchId`) lists branch roles the caller can see. */
  list(filter: RoleListFilter = {}): Observable<RoleListItem[]> {
    const params: Record<string, string> = {};
    if (filter.scope) params['scope'] = filter.scope;
    if (filter.branchId) params['branchId'] = filter.branchId;
    return this.http.get<RoleListItem[]>(BASE_URL, { params });
  }

  /** Members allocated to a branch (for branch administrators picking who to assign). */
  branchMembers(branchId: string): Observable<BranchMember[]> {
    return this.http.get<BranchMember[]>(`${environment.apiBaseUrl}/branches/${branchId}/members`);
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
