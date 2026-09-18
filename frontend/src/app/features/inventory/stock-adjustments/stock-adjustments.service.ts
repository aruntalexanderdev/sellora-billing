import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { CreateStockAdjustmentRequest, StockAdjustmentResult } from './stock-adjustments.models';

@Injectable({ providedIn: 'root' })
export class StockAdjustmentsService {
  private readonly http = inject(HttpClient);

  create(request: CreateStockAdjustmentRequest): Observable<StockAdjustmentResult> {
    return this.http.post<StockAdjustmentResult>(`${environment.apiBaseUrl}/stock-adjustments`, request);
  }
}
