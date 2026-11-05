import { Injectable } from '@angular/core';
import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import {
  BulkProductGstRuleApplied,
  BulkProductGstRulePreview,
  GstClassification,
  InventoryValuationSummary,
  MasterProductSuggestionPage,
  Product,
  ProductCreateDto,
  ProductGstRule,
  ProductPriceTracking,
  ProductUpdateDto,
  ReorderAlertListing,
  ReorderMode,
  TrackedProduct
} from '../models/models';
import { nayaxUnavailabilityOfReason } from '../provider/nayax-availability';
import { ConfigService } from './config.service';

export interface ProductFilters {
  search?: string;
  categoryId?: number;
  // No `supplierId` (issue #787, A1 of #786): a product has no supplier, so `GET /api/products` and
  // the reorder-alert listing accept no such narrowing any more.
  machineId?: number;
  lowStockOnly?: boolean;
  /**
   * Whether to include a deactivated product in the answer (issue #737): `GET /api/products`
   * answers the working catalogue - active products only - unless this is set, because a
   * deactivated product is retired out of the lists, the pickers and the reorder alerts while its
   * history stays intact. The catalogue page passes this so Reactivate stays reachable after a
   * product is deactivated.
   */
  includeRetired?: boolean;
}

/** `InventoryApi.Adapters.Mapping.ReorderListingMetadataHeaders.Mode`. */
const REORDER_MODE_HEADER = 'X-Reorder-Mode';

/** `ReorderListingMetadataHeaders.MachineNeedUnavailableReason`. */
const MACHINE_NEED_UNAVAILABLE_REASON_HEADER = 'X-Reorder-Machine-Need-Unavailable-Reason';

/** The published mode code, or `null` for an absent or unrecognised one - never a guess. */
function reorderMode(header: string | null): ReorderMode | null {
  return header === 'StorageOnly' || header === 'MachineAware' ? header : null;
}

function reorderAlertListing(response: HttpResponse<Product[]>): ReorderAlertListing {
  const mode = reorderMode(response.headers.get(REORDER_MODE_HEADER));
  return {
    products: response.body ?? [],
    mode,
    // Only a storage-only listing has machines missing from it, so a reason beside any other mode
    // is ignored rather than reported as something this listing left out.
    machineNeedUnavailableReason:
      mode === 'StorageOnly'
        ? nayaxUnavailabilityOfReason(response.headers.get(MACHINE_NEED_UNAVAILABLE_REASON_HEADER))
        : null
  };
}

@Injectable({ providedIn: 'root' })
export class ProductService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/products`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  getAll(filters: ProductFilters = {}): Observable<Product[]> {
    let params = new HttpParams();
    if (filters.search) params = params.set('search', filters.search);
    if (filters.categoryId !== undefined) params = params.set('categoryId', filters.categoryId);
    if (filters.machineId !== undefined) params = params.set('machineId', filters.machineId);
    if (filters.lowStockOnly) params = params.set('lowStockOnly', true);
    if (filters.includeRetired) params = params.set('includeRetired', true);
    return this.http.get<Product[]>(this.baseUrl, { params });
  }

  get(id: number): Observable<Product> {
    return this.http.get<Product>(`${this.baseUrl}/${id}`);
  }

  /**
   * The reorder alerts, with the mode they were calculated in (issue #727). The body is the same
   * JSON array it has always been; the mode and, in storage-only mode, the reason the machines were
   * not read, are additive response headers (issue #726), so the full response is observed rather
   * than the body alone. A header this build does not recognise, or a response that carries none at
   * all, leaves the metadata `null` rather than guessing a mode - a screen then shows the rows
   * without labelling them.
   */
  getLowStock(filters: ProductFilters = {}): Observable<ReorderAlertListing> {
    let params = new HttpParams();
    if (filters.search) params = params.set('search', filters.search);
    if (filters.categoryId !== undefined) params = params.set('categoryId', filters.categoryId);
    return this.http
      .get<Product[]>(`${this.baseUrl}/alerts/low-stock`, { params, observe: 'response' })
      .pipe(map((response) => reorderAlertListing(response)));
  }

  getInventoryValuationSummary(): Observable<InventoryValuationSummary> {
    return this.http.get<InventoryValuationSummary>(`${this.baseUrl}/inventory-value-summary`);
  }

  update(id: number, payload: ProductUpdateDto): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${id}`, payload);
  }

  /** Creates a product this business maintains itself (issue #737). */
  create(payload: ProductCreateDto): Observable<Product> {
    return this.http.post<Product>(this.baseUrl, payload);
  }

  /**
   * One bounded page of the shared master product catalogue, for the New Product Name suggestions
   * (`GET api/products/master-product-suggestions`, issue #1049 - used by issue #1052).
   *
   * The term is sent trimmed and nothing else is sent: the page size, the ordering and the maximum
   * term length are the server's decisions, and it answers with the page size it actually served. A
   * term the server refuses (shorter than 2 characters, longer than its own maximum) is a `400`, so
   * the caller decides what to ask rather than this method wording the rule a second time.
   */
  getMasterProductSuggestions(term: string): Observable<MasterProductSuggestionPage> {
    const params = new HttpParams().set('q', term.trim());
    return this.http.get<MasterProductSuggestionPage>(`${this.baseUrl}/master-product-suggestions`, { params });
  }

  /** The product's configured GST rule (issue #430); it is its own resource, not a catalogue field. */
  getGstRule(id: number): Observable<ProductGstRule> {
    return this.http.get<ProductGstRule>(`${this.baseUrl}/${id}/gst-rule`);
  }

  setGstRule(id: number, gstRule: GstClassification): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${id}/gst-rule`, { gstRule });
  }

  /**
   * How many of the business's products carry no GST rule (issue #1076) - the scope "Set GST rule
   * in bulk" asks the operator to confirm. It writes nothing.
   *
   * A `POST` with no body because that is the API's own shape: the count it answers is the
   * confirmation {@link setBulkGstRule} is checked against, and a cacheable `GET` would hand the
   * dialog a confirmation for a catalogue it never read. No filter, category, page or product id is
   * sent, because the scope is the whole catalogue of the caller's own business.
   */
  previewBulkGstRule(): Observable<BulkProductGstRulePreview> {
    return this.http.post<BulkProductGstRulePreview>(`${this.baseUrl}/gst-rule/bulk-preview`, {});
  }

  /**
   * Sets one GST rule on every product of the business that carries none (issue #1076), and reports
   * how many products took it.
   *
   * The body carries the target rule and the count that was confirmed, and nothing else: the server
   * re-counts inside its own transaction and answers `409` when the count has moved, so a stale
   * confirmation never configures a set the operator did not see.
   */
  setBulkGstRule(
    gstRule: GstClassification,
    expectedEligibleCount: number
  ): Observable<BulkProductGstRuleApplied> {
    return this.http.post<BulkProductGstRuleApplied>(`${this.baseUrl}/gst-rule/bulk`, {
      gstRule,
      expectedEligibleCount
    });
  }

  /**
   * Where this product has got to in the shared supplier price catalogue (issue #938): whether the
   * business tracks it, the mapping's status, and - only while it is matched - the master product's
   * name and size. Its own resource, not a catalogue field, for the reason the GST rule is one.
   */
  getPriceTracking(id: number): Observable<ProductPriceTracking> {
    return this.http.get<ProductPriceTracking>(`${this.baseUrl}/${id}/price-tracking`);
  }

  /**
   * Turns the "Track supplier prices" switch on or off (issue #938). The body carries one boolean:
   * the search text is the product's own name and unit label, read by the server, and no status or
   * master product is submittable at all.
   */
  setPriceTracking(id: number, tracked: boolean): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${id}/price-tracking`, { tracked });
  }

  /** The business's tracked products, for the catalogue listing's Tracked column (issue #938). */
  getTrackedProducts(): Observable<TrackedProduct[]> {
    return this.http.get<TrackedProduct[]>(`${this.baseUrl}/price-tracking`);
  }

  delete(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }
}
