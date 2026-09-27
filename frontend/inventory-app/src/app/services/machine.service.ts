import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Machine,
  NayaxMachineStockApplyResponse,
  NayaxMachineStockSyncPreview,
  Product
} from '../models/models';
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

  syncRestock(id: number): Observable<NayaxMachineStockSyncPreview> {
    return this.http.post<NayaxMachineStockSyncPreview>(`${this.baseUrl}/${id}/sync-restock`, {});
  }

  applySyncRestock(id: number, eventIds: number[]): Observable<NayaxMachineStockApplyResponse> {
    return this.http.post<NayaxMachineStockApplyResponse>(`${this.baseUrl}/${id}/sync-restock/apply`, { eventIds });
  }
}
