import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { PermissionCatalogEntry } from './permissions.models';

@Injectable({ providedIn: 'root' })
export class PermissionsService {
  private readonly http = inject(HttpClient);

  list(): Observable<PermissionCatalogEntry[]> {
    return this.http.get<PermissionCatalogEntry[]>(`${environment.apiBaseUrl}/permissions`);
  }
}
