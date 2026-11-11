import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import type { CreateVehicleRequest, UpdateVehicleRequest, Vehicle, VehicleQuery } from './vehicles.models';

const BASE_URL = `${environment.apiBaseUrl}/vehicles`;

@Injectable({ providedIn: 'root' })
export class VehiclesService {
  private readonly http = inject(HttpClient);

  list(query: VehicleQuery = {}): Observable<Vehicle[]> {
    let params = new HttpParams();
    if (query.unassigned) params = params.set('unassigned', true);
    else if (query.assignedBranchId) params = params.set('assignedBranchId', query.assignedBranchId);
    if (query.vehicleType) params = params.set('vehicleType', query.vehicleType);
    if (query.includeInactive) params = params.set('includeInactive', true);
    return this.http.get<Vehicle[]>(BASE_URL, { params });
  }

  get(id: string): Observable<Vehicle> {
    return this.http.get<Vehicle>(`${BASE_URL}/${id}`);
  }

  create(request: CreateVehicleRequest): Observable<Vehicle> {
    return this.http.post<Vehicle>(BASE_URL, request);
  }

  /** PUT also reassigns: a changed assignedBranchId (null = central) is refused while the vehicle holds stock or has open transfers. */
  update(id: string, request: UpdateVehicleRequest): Observable<Vehicle> {
    return this.http.put<Vehicle>(`${BASE_URL}/${id}`, request);
  }

  setStatus(id: string, isActive: boolean, reason: string): Observable<Vehicle> {
    return this.http.post<Vehicle>(`${BASE_URL}/${id}/status`, { isActive, reason });
  }
}
