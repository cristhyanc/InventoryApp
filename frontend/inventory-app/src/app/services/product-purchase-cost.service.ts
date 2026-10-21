import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ProductPurchaseCostSummary } from '../models/product-purchase-cost-summary';
import { ConfigService } from './config.service';

/**
 * The frontend client for the bulk product purchase-cost summary
 * (`GET /api/products/purchase-cost-summary`, issue #788, B of #786): what every product was last
 * bought for and what it was cheapest at, with the supplier of each, in one request instead of one
 * `GET /api/products/{id}/price-history` per product.
 *
 * **It fetches and nothing else.** The last and cheapest costs, their suppliers, their dates and
 * every tie-break between them are decided by the backend's one authoritative calculation
 * (`SupplierPriceComparisonPolicy`), so this service must never sort, filter, pick a cheapest, or
 * derive a saving - a second copy of any of those would let the product screens disagree with a
 * product's own price-history view.
 *
 * The endpoint requires `Dashboard.ViewFinancials` and answers `403` without it, so a caller that
 * may be an Operator must handle that refusal rather than treating an error as "no purchases".
 * Products → Needs Ordering (issue #790, D of #786) does not ask at all without the capability,
 * and reports a failed request as "Unavailable" with a Retry rather than as "no purchases".
 *
 * The remaining screens are issues #789 and #791 (C and E of #786).
 */
@Injectable({ providedIn: 'root' })
export class ProductPurchaseCostService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/products/purchase-cost-summary`;
  }

  constructor(private readonly http: HttpClient, private readonly config: ConfigService) {}

  /**
   * The summaries for `productIds`, or - with no ids at all - for every product of the business,
   * retired products included.
   *
   * The ids are sent as repeated `productIds` query values, which is what the endpoint binds. A
   * product with no recorded purchase history is **absent** from the answer whether it was asked
   * for or not, so a caller matches rows up by `productId` and shows "no purchases recorded" for a
   * product it does not find; the array is normally shorter than the catalogue.
   */
  getSummaries(productIds: readonly number[] = []): Observable<ProductPurchaseCostSummary[]> {
    let params = new HttpParams();
    for (const productId of productIds) {
      params = params.append('productIds', productId);
    }
    return this.http.get<ProductPurchaseCostSummary[]>(this.baseUrl, { params });
  }
}
