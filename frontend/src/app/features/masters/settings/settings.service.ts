import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { BusinessSettings, UpdateBusinessSettingsRequest } from './settings.models';

const BASE_URL = `${environment.apiBaseUrl}/settings`;

@Injectable({ providedIn: 'root' })
export class SettingsService {
  private readonly http = inject(HttpClient);

  get(): Observable<BusinessSettings> {
    return this.http.get<BusinessSettings>(BASE_URL);
  }

  update(request: UpdateBusinessSettingsRequest): Observable<BusinessSettings> {
    return this.http.put<BusinessSettings>(BASE_URL, request);
  }
}
