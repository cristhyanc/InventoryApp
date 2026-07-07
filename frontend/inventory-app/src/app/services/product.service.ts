import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Product, ProductCreateDto, ProductUpdateDto } from '../models/models';

export interface ProductFilters {
  search?: string;
  categoryId?: number;
  supplierId?: number;
  lowStockOnly?: boolean;
}

@Injectable({ providedIn: 'root' })
export class ProductService {
  private readonly baseUrl = '/api/products';

  constructor(private http: HttpClient) {}

  getAll(filters: ProductFilters = {}): Observable<Product[]> {
    let params = new HttpParams();
    if (filters.search) params = params.set('search', filters.search);
    if (filters.categoryId !== undefined) params = params.set('categoryId', filters.categoryId);
    if (filters.supplierId !== undefined) params = params.set('supplierId', filters.supplierId);
    if (filters.lowStockOnly) params = params.set('lowStockOnly', true);
    return this.http.get<Product[]>(this.baseUrl, { params });
  }

  get(id: number): Observable<Product> {
    return this.http.get<Product>(`${this.baseUrl}/${id}`);
  }

  getLowStock(): Observable<Product[]> {
    return this.http.get<Product[]>(`${this.baseUrl}/alerts/low-stock`);
  }

  create(payload: ProductCreateDto): Observable<Product> {
    return this.http.post<Product>(this.baseUrl, payload);
  }

  importProducts(): Observable<boolean> {
    return this.http.post<boolean>(`${this.baseUrl}/importProducts`, {});
  }

  update(id: number, payload: ProductUpdateDto): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${id}`, payload);
  }

  delete(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }
}
