import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Product, ProductUpdateDto } from '../models/models';
import { ConfigService } from './config.service';

export interface ProductFilters {
  search?: string;
  categoryId?: number;
  supplierId?: number;
  machineId?: number;
  lowStockOnly?: boolean;
}

@Injectable({ providedIn: 'root' })
export class ProductService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/products`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  getAll(filters: ProductFilters = {}): Observable<Product[]> {
    let params = new HttpParams();
    if (filters.search) params = params.set('search', filters.search);
    if (filters.categoryId !== undefined) params = params.set('categoryId', filters.categoryId);
    if (filters.supplierId !== undefined) params = params.set('supplierId', filters.supplierId);
    if (filters.machineId !== undefined) params = params.set('machineId', filters.machineId);
    if (filters.lowStockOnly) params = params.set('lowStockOnly', true);
    return this.http.get<Product[]>(this.baseUrl, { params });
  }

  get(id: number): Observable<Product> {
    return this.http.get<Product>(`${this.baseUrl}/${id}`);
  }

  getLowStock(filters: ProductFilters = {}): Observable<Product[]> {
    let params = new HttpParams();
    if (filters.search) params = params.set('search', filters.search);
    if (filters.categoryId !== undefined) params = params.set('categoryId', filters.categoryId);
    if (filters.supplierId !== undefined) params = params.set('supplierId', filters.supplierId);
    return this.http.get<Product[]>(`${this.baseUrl}/alerts/low-stock`, { params });
  }

  update(id: number, payload: ProductUpdateDto): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${id}`, payload);
  }

  delete(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }
}
