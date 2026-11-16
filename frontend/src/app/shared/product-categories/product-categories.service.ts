import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import type { ProductCategory, ProductSubcategory, UpsertClassificationRequest } from './product-categories.models';

const BASE_URL = `${environment.apiBaseUrl}/product-categories`;

@Injectable({ providedIn: 'root' })
export class ProductCategoriesService {
  private readonly http = inject(HttpClient);

  list(includeInactive = false): Observable<ProductCategory[]> {
    const params: Record<string, string> = includeInactive ? { includeInactive: 'true' } : {};
    return this.http.get<ProductCategory[]>(BASE_URL, { params });
  }

  create(request: UpsertClassificationRequest): Observable<ProductCategory> {
    return this.http.post<ProductCategory>(BASE_URL, request);
  }

  update(id: string, request: UpsertClassificationRequest): Observable<ProductCategory> {
    return this.http.put<ProductCategory>(`${BASE_URL}/${id}`, request);
  }

  setActive(id: string, isActive: boolean): Observable<ProductCategory> {
    return this.http.post<ProductCategory>(`${BASE_URL}/${id}/status`, { isActive });
  }

  createSubcategory(categoryId: string, request: UpsertClassificationRequest): Observable<ProductSubcategory> {
    return this.http.post<ProductSubcategory>(`${BASE_URL}/${categoryId}/subcategories`, request);
  }

  updateSubcategory(categoryId: string, id: string, request: UpsertClassificationRequest): Observable<ProductSubcategory> {
    return this.http.put<ProductSubcategory>(`${BASE_URL}/${categoryId}/subcategories/${id}`, request);
  }

  setSubcategoryActive(categoryId: string, id: string, isActive: boolean): Observable<ProductSubcategory> {
    return this.http.post<ProductSubcategory>(`${BASE_URL}/${categoryId}/subcategories/${id}/status`, { isActive });
  }
}
