import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { Machine, Product } from '../models/models';
import { ConfigService } from './config.service';

@Injectable({
  providedIn: 'root'
})
export class MachineService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/machines`;
  }

  constructor(private http: HttpClient, private config: ConfigService) { }

  getAll(): Observable<Machine[]> {
    return this.http.get<Machine[]>(this.baseUrl);
  }

  get(id: number): Observable<Machine> {
    return this.http.get<Machine>(`${this.baseUrl}/${id}`);
  }

    getProducts(id: number): Observable<Product[]> {
    return this.http.get<Product[]>(`${this.baseUrl}/${id}/products`);
  }
}
