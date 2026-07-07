import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { StockAdjustment, StockAdjustmentDto } from '../models/models';

@Injectable({ providedIn: 'root' })
export class StockService {
  constructor(private http: HttpClient) {}

  history(productId: number): Observable<StockAdjustment[]> {
    return this.http.get<StockAdjustment[]>(`/api/products/${productId}/stock`);
  }

  adjust(productId: number, payload: StockAdjustmentDto): Observable<StockAdjustment> {
    return this.http.post<StockAdjustment>(`/api/products/${productId}/stock`, payload);
  }
}
