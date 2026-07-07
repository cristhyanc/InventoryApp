import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Category } from '../models/models';

@Injectable({ providedIn: 'root' })
export class CategoryService {
  private readonly baseUrl = '/api/categories';

  constructor(private http: HttpClient) {}

  getAll(): Observable<Category[]> {
    return this.http.get<Category[]>(this.baseUrl);
  }

  get(id: number): Observable<Category> {
    return this.http.get<Category>(`${this.baseUrl}/${id}`);
  }

  create(payload: { name: string; description?: string | null }): Observable<Category> {
    return this.http.post<Category>(this.baseUrl, payload);
  }

  update(id: number, payload: { name: string; description?: string | null }): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${id}`, payload);
  }

  delete(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }
}
