import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Category, CategoryWriteDto } from '../models/models';
import { ConfigService } from './config.service';

@Injectable({ providedIn: 'root' })
export class CategoryService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/categories`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  getAll(): Observable<Category[]> {
    return this.http.get<Category[]>(this.baseUrl);
  }

  get(id: number): Observable<Category> {
    return this.http.get<Category>(`${this.baseUrl}/${id}`);
  }

  /** Creates a category this business maintains itself (issue #737). */
  create(payload: CategoryWriteDto): Observable<Category> {
    return this.http.post<Category>(this.baseUrl, payload);
  }

  /** Renames a category or changes its description (issue #737). */
  update(id: number, payload: CategoryWriteDto): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${id}`, payload);
  }

  /** Deletes a category no product uses (issue #737). */
  delete(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }
}
