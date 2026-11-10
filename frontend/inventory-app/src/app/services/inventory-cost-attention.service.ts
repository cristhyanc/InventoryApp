import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * One product whose inventory-cost recalculation failed and is still waiting to be retried, exactly
 * as `GET api/inventory-cost-attention` publishes it.
 */
export interface ProductNeedingCostAttention {
  productId: number;
  /** The product's current name, or `null` when no product with that id is stored. */
  productName: string | null;
  /** `data_quality` when the product's history has a fatal costing gap, `product_missing` otherwise. */
  failureCode: string;
  attemptCount: number;
  lastAttemptAtUtc: string | null;
}

/**
 * The Dashboard's "costs need attention" read. A product whose cost history cannot be recalculated
 * no longer fails the Nayax sales sync; it is reported here, by product, instead.
 */
@Injectable({ providedIn: 'root' })
export class InventoryCostAttentionService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/inventory-cost-attention`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  list(): Observable<ProductNeedingCostAttention[]> {
    return this.http.get<ProductNeedingCostAttention[]>(this.baseUrl);
  }
}
