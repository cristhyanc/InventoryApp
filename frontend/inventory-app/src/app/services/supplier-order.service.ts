import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { SupplierOrder, SupplierOrderCreateDto } from '../models/models';
import { ConfigService } from './config.service';

@Injectable({ providedIn: 'root' })
export class SupplierOrderService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/supplierorders`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  getActive(): Observable<SupplierOrder[]> {
    return this.http.get<SupplierOrder[]>(this.baseUrl);
  }

  getById(id: number): Observable<SupplierOrder> {
    return this.http.get<SupplierOrder>(`${this.baseUrl}/${id}`);
  }

  create(payload: SupplierOrderCreateDto): Observable<SupplierOrder> {
    return this.http.post<SupplierOrder>(this.baseUrl, payload);
  }

  cancel(id: number): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${id}/cancel`, {});
  }
}