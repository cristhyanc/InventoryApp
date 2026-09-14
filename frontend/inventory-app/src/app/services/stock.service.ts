import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { RestockCostSuggestion, StockAdjustment, StockAdjustmentDto } from '../models/models';
import { ConfigService } from './config.service';

@Injectable({ providedIn: 'root' })
export class StockService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/products`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  history(productId: number): Observable<StockAdjustment[]> {
    return this.http.get<StockAdjustment[]>(`${this.baseUrl}/${productId}/stock`);
  }

  restockCostSuggestion(productId: number): Observable<RestockCostSuggestion> {
    return this.http.get<RestockCostSuggestion>(`${this.baseUrl}/${productId}/stock/restock-cost-suggestion`);
  }

  adjust(productId: number, payload: StockAdjustmentDto): Observable<StockAdjustment> {
    return this.http.post<StockAdjustment>(`${this.baseUrl}/${productId}/stock`, payload);
  }
}
