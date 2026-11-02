import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, forkJoin, map, of } from 'rxjs';
import { ProductPurchaseCostSummary } from '../models/product-purchase-cost-summary';
import { ConfigService } from './config.service';

/**
 * The most product ids one `GET` from {@link ProductPurchaseCostService.getSummariesBatched} carries
 * (issue #1017 repair). The ids travel as repeated `productIds` query values, so the request line
 * grows by `productIds=<id>&` per id, and Kestrel refuses a request line longer than its default
 * `MaxRequestLineSize` of 8,192 bytes. At 100 ids even the widest `long` (20 characters with its
 * sign) keeps the query at about 3.2 kB, under half that limit; 310 real 15-digit ids in one
 * request already exceeded it. This is a frontend safety bound, not an endpoint rule: the endpoint
 * publishes none.
 */
export const PURCHASE_COST_MAX_IDS_PER_REQUEST = 100;

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
 * The home Dashboard's Reorder Alerts table is its first caller (issue #789, C of #786), and All
 * Products (issue #791, E of #786) calls it through `ProductPurchaseCostsComponent`.
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

  /**
   * The summaries for exactly `productIds`, read as one logical load split into `GET`s of at most
   * {@link PURCHASE_COST_MAX_IDS_PER_REQUEST} ids each, so a long id list can never build a request
   * line the server refuses. Duplicate ids are collapsed before the split, so no id is asked twice.
   *
   * Unlike {@link getSummaries}, an **empty** list sends nothing and answers `[]`: an empty batch
   * would mean "every product of the business", which no caller of this method is asking for.
   * The answers are concatenated untouched - nothing is sorted, filtered or picked here - and if any
   * batch fails the whole load fails, so a caller shows its unavailable state rather than a mix of
   * real costs and gaps that would read as "no purchases recorded".
   */
  getSummariesBatched(productIds: readonly number[]): Observable<ProductPurchaseCostSummary[]> {
    const distinct = [...new Set(productIds)];
    if (distinct.length === 0) return of([]);

    const batches: number[][] = [];
    for (let index = 0; index < distinct.length; index += PURCHASE_COST_MAX_IDS_PER_REQUEST) {
      batches.push(distinct.slice(index, index + PURCHASE_COST_MAX_IDS_PER_REQUEST));
    }
    return forkJoin(batches.map((batch) => this.getSummaries(batch))).pipe(map((answers) => answers.flat()));
  }
}
