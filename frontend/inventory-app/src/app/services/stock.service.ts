import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  RestockCostSuggestion,
  StockAdjustment,
  StockAdjustmentDto,
  StockHistoryFilters,
  StockHistoryPage
} from '../models/models';
import { ConfigService } from './config.service';

@Injectable({ providedIn: 'root' })
export class StockService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/products`;
  }

  private get stockHistoryUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/stock-history`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  history(productId: number): Observable<StockAdjustment[]> {
    return this.http.get<StockAdjustment[]>(`${this.baseUrl}/${productId}/stock`);
  }

  /**
   * One bounded page of the global stock history across products (issue #384), optionally filtered.
   * Only the filters that are set are sent, so an empty filter is the all-products view; the server
   * decides the ordering (newest first), the Sydney-day date boundaries and the maximum page size.
   */
  historyPage(filters: StockHistoryFilters = {}): Observable<StockHistoryPage> {
    let params = new HttpParams();
    if (filters.productId != null) params = params.set('productId', filters.productId);
    if (filters.from) params = params.set('from', filters.from);
    if (filters.to) params = params.set('to', filters.to);
    if (filters.reason != null) params = params.set('reason', filters.reason);
    if (filters.machineId != null) params = params.set('machineId', filters.machineId);
    if (filters.source != null) params = params.set('source', filters.source);
    if (filters.page != null) params = params.set('page', filters.page);
    if (filters.pageSize != null) params = params.set('pageSize', filters.pageSize);
    return this.http.get<StockHistoryPage>(this.stockHistoryUrl, { params });
  }

  restockCostSuggestion(productId: number): Observable<RestockCostSuggestion> {
    return this.http.get<RestockCostSuggestion>(`${this.baseUrl}/${productId}/stock/restock-cost-suggestion`);
  }

  adjust(productId: number, payload: StockAdjustmentDto): Observable<StockAdjustment> {
    return this.http.post<StockAdjustment>(`${this.baseUrl}/${productId}/stock`, payload);
  }
}
