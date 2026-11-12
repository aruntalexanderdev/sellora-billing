import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ContextService } from '../../core/context/context.service';
import type { Dashboard } from './dashboard.models';

const BASE_URL = `${environment.apiBaseUrl}/dashboard`;

@Injectable({ providedIn: 'root' })
export class DashboardService {
  private readonly http = inject(HttpClient);
  private readonly context = inject(ContextService);

  /** FI-D11: the company view needs company-level `Reports.Sales`. */
  get(): Observable<Dashboard> {
    const params: Record<string, string> = this.context.companyViewActive('Reports.Sales') ? { scope: 'company' } : {};
    return this.http.get<Dashboard>(BASE_URL, { params });
  }
}
