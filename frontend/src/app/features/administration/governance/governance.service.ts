import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PagedResult } from '../../../core/api.models';
import type { AuditEvent } from '../users/user-access/user-access.models';
import type {
  AddGovernanceMemberRequest, ChangeGovernanceTitleRequest, EndGovernanceMemberRequest, GovernanceMember, GovernancePermission,
  GrantAdministratorRequest, RemoveAdministratorRequest, SetGovernancePermissionsRequest,
} from './governance.models';

const BASE_URL = `${environment.apiBaseUrl}/governance`;

@Injectable({ providedIn: 'root' })
export class GovernanceService {
  private readonly http = inject(HttpClient);

  permissions(): Observable<GovernancePermission[]> {
    return this.http.get<GovernancePermission[]>(`${BASE_URL}/permissions`);
  }

  members(includeEnded: boolean): Observable<GovernanceMember[]> {
    return this.http.get<GovernanceMember[]>(`${BASE_URL}/members`, { params: { includeEnded } });
  }

  addMember(request: AddGovernanceMemberRequest): Observable<GovernanceMember> {
    return this.http.post<GovernanceMember>(`${BASE_URL}/members`, request);
  }

  changeTitle(id: string, request: ChangeGovernanceTitleRequest): Observable<GovernanceMember> {
    return this.http.post<GovernanceMember>(`${BASE_URL}/members/${id}/title`, request);
  }

  setPermissions(id: string, request: SetGovernancePermissionsRequest): Observable<GovernanceMember> {
    return this.http.put<GovernanceMember>(`${BASE_URL}/members/${id}/permissions`, request);
  }

  endMember(id: string, request: EndGovernanceMemberRequest): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/members/${id}/end`, request);
  }

  grantAdministrator(request: GrantAdministratorRequest): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/administrators`, request);
  }

  removeAdministrator(userId: string, request: RemoveAdministratorRequest): Observable<void> {
    return this.http.post<void>(`${BASE_URL}/administrators/${userId}/remove`, request);
  }

  events(page: number, pageSize = 20): Observable<PagedResult<AuditEvent>> {
    return this.http.get<PagedResult<AuditEvent>>(`${BASE_URL}/events`, { params: { page, pageSize } });
  }
}
