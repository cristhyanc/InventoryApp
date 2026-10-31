import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, forkJoin, map } from 'rxjs';
import { CheapestSupplierSummary } from '../models/cheapest-supplier';
import { ConfigService } from './config.service';

/**
 * The most product ids one request may carry, which is the backend's own bound
 * (`CheapestSupplierSummaryRequestPolicy.MaxProductIds`, issue #930 under the review finding of
 * 27 October 2026). A page with more rows than this is split here, so every caller splits the same
 * way and no page has to know the number.
 */
export const CHEAPEST_SUPPLIER_MAX_IDS = 200;

/**
 * The frontend client for the shared cheapest-supplier summary
 * (`POST /api/cheapest-suppliers/summary`, issue #930, parent #817): which of the business's own
 * suppliers is cheapest per vending unit for each product on a page.
 *
 * **It fetches, batches, and nothing else.** Which supplier is cheapest, what each offer costs per
 * unit on either GST basis, which basis this business is shown, why an offer could not be compared
 * and which offers tie are all decided by the backend's one authoritative comparison
 * (`CheapestUnitOfferPolicy`, issue #925). This service must never divide a pack price, convert
 * between GST bases, rank offers or pick a cheapest: a second copy of any of that would let the
 * three pages that show this column disagree with each other, which is the whole reason the
 * endpoint is shared.
 *
 * It is a `POST` that changes nothing, and only because of the id list: a page of up to 200 ids
 * does not belong in a query string. One consequence is worth knowing at the call site - a
 * read-only support session refuses every non-`GET` request, so the column reads as unavailable
 * there rather than as "no prices".
 */
@Injectable({ providedIn: 'root' })
export class CheapestSupplierService {
  private get summaryUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/cheapest-suppliers/summary`;
  }

  constructor(private readonly http: HttpClient, private readonly config: ConfigService) {}

  /**
   * The summary for `productIds`, in as few requests as the batch size allows.
   *
   * Duplicate ids are collapsed before the split, so a page listing a product twice asks about it
   * once and the two halves of a batch boundary cannot both carry it. A product that is not the
   * caller's business's is absent from the answer rather than refused, so callers match rows up by
   * `productId`.
   */
  getSummary(productIds: readonly number[]): Observable<CheapestSupplierSummary> {
    const distinct = [...new Set(productIds)];
    const batches: number[][] = [];
    for (let index = 0; index < distinct.length; index += CHEAPEST_SUPPLIER_MAX_IDS) {
      batches.push(distinct.slice(index, index + CHEAPEST_SUPPLIER_MAX_IDS));
    }

    return forkJoin(
      batches.map((batch) => this.http.post<CheapestSupplierSummary>(this.summaryUrl, { productIds: batch }))
    ).pipe(
      map((answers) => ({
        // The basis is one fact about the business, so every batch answers the same one; the first
        // is taken rather than re-derived, because deriving it here would be deciding it.
        displayBasis: answers[0].displayBasis,
        products: answers.flatMap((answer) => answer.products)
      }))
    );
  }
}
