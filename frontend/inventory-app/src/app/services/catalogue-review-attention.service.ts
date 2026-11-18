import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/** One Nayax product that is selling and is not in this business's products (issue #1352). */
export interface UnimportedProviderProductExample {
  /**
   * Nayax's own product id, as the opaque text a link stores. It is deliberately a string and never
   * a local product id: the whole point of the alert is that no local product carries it.
   */
  nayaxProductId: string;

  /** The name the most recent sale carried, or `null` when that sale stored none. */
  productName: string | null;
}

/**
 * `GET api/catalogue-review/attention`: how many distinct Nayax products the business has sold in
 * the last thirty business dates without holding a product for them, and the most recently sold few
 * of them by name. It carries no amount, price, cost or quantity.
 */
export interface UnimportedProviderProductAttention {
  unimportedProductCount: number;
  examples: UnimportedProviderProductExample[];
}

/**
 * The Dashboard's "Nayax products are selling but aren't in your products" read (issue #1352).
 *
 * It makes no Nayax call: the answer comes from the business's own stored sales, which were saved
 * with no local product precisely because nothing carried the provider's id for them. Importing
 * those products through Import products is what clears it.
 */
@Injectable({ providedIn: 'root' })
export class CatalogueReviewAttentionService {
  private get url(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/catalogue-review/attention`;
  }

  constructor(private readonly http: HttpClient, private readonly config: ConfigService) {}

  read(): Observable<UnimportedProviderProductAttention> {
    return this.http.get<UnimportedProviderProductAttention>(this.url);
  }
}
