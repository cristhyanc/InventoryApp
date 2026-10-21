import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * What the operator asked the review to do with one unlinked provider entry. The numbers are the
 * contract the API deserializes, so they must stay in step with the backend
 * `Inventory.Domain.Providers.CatalogueReviewAction` (issue #738).
 *
 * `Defer` is the honest default: an entry nobody decided about is left alone, never linked by
 * omission.
 */
export enum CatalogueReviewAction {
  Defer = 1,
  LinkExisting = 2,
  CreateNew = 3
}

/**
 * One field whose provider value the operator negotiates rather than inherits, matching
 * `Inventory.Domain.Providers.NegotiableCatalogueField`.
 *
 * `UnitPrice` is retired since issue #792 (A2 of #786): a product carries no selling price, the API
 * proposes none, and a decision naming it is refused. `UnitVolume` is declared by the API and never
 * carries a provider value: the Lynx product contract publishes no unit/volume label, so none is
 * proposed and this screen offers no choice about it (issue #739's out-of-scope note and the
 * owner's decision of 20 October 2026). So the review offers no choices at all today, and this enum
 * exists to keep the wire numbers in step with the backend's.
 */
export enum NegotiableCatalogueField {
  UnitVolume = 2
}

/**
 * What the operator decided about one negotiable field's provider value, matching
 * `Inventory.Domain.Providers.ProviderFieldDecision`. The same two answers serve "keep mine or take
 * the provider's" when a row is first linked and "skip or accept" when a linked row's provider
 * value has since changed.
 */
export enum ProviderFieldDecision {
  KeepLocal = 1,
  TakeProvider = 2
}

/** The `field` names a provider-owned automatic change carries (`ProviderManagedCatalogueFields`). */
export const PROVIDER_MANAGED_NAME_FIELD = 'name';
export const PROVIDER_MANAGED_CATEGORY_FIELD = 'categoryId';

/** One negotiable field the review is asking about: `Inventory.Application.CatalogueReview.CatalogueReviewFieldDto`. */
export interface CatalogueReviewField {
  field: string;
  localValue: string | null;
  providerValue: string | null;
}

/**
 * One provider-owned field change the apply will make without asking - a linked row's name, and a
 * product's category. The provider owns these, so the review lists them rather than offering them.
 */
export interface CatalogueReviewAutomaticChange {
  field: string;
  localValue: string | null;
  providerValue: string | null;
}

/** One provider product group in the review. */
export interface CatalogueReviewCategory {
  itemId: number;
  externalId: string;
  providerName: string;
  linkedCategoryId: number | null;
  localName: string | null;
  automaticChanges: CatalogueReviewAutomaticChange[];
  isLinked: boolean;
}

/** One provider product in the review. */
export interface CatalogueReviewProduct {
  itemId: number;
  externalId: string;
  providerName: string;
  providerGroupExternalId: string | null;
  linkedProductId: number | null;
  localName: string | null;
  unresolvedSaleCount: number;
  automaticChanges: CatalogueReviewAutomaticChange[];
  choices: CatalogueReviewField[];
  isLinked: boolean;
}

/**
 * A started provider catalogue review: the provider's catalogue as it was read, reconciled against
 * the business's own rows (`CatalogueReviewDto`, issue #738). Starting it writes no product,
 * category, price or sale - the `reviewId` is what the apply is authorised by.
 */
export interface CatalogueReview {
  reviewId: number;
  startedAtUtc: string;
  categories: CatalogueReviewCategory[];
  products: CatalogueReviewProduct[];
  unitVolumeNote: string;
  unlinkedCategoryCount: number;
  unlinkedProductCount: number;
  unresolvedSaleCount: number;
}

/** What the operator decided about one negotiable field of one reviewed product. */
export interface CatalogueReviewFieldDecisionRequest {
  field: NegotiableCatalogueField;
  decision: ProviderFieldDecision;
}

/** What the operator decided about one reviewed entry. */
export interface CatalogueReviewDecisionRequest {
  itemId: number;
  action: CatalogueReviewAction;
  localRowId?: number | null;
  fields?: CatalogueReviewFieldDecisionRequest[];
}

/** The apply request: decisions and nothing else. Every provider value comes from the stored review. */
export interface ApplyCatalogueReviewRequest {
  reviewId: number;
  decisions: CatalogueReviewDecisionRequest[];
}

/** One link the apply recorded. `created` says whether the local row is new. */
export interface CatalogueReviewLinkApplied {
  kind: string;
  externalId: string;
  localRowId: number;
  name: string;
  created: boolean;
}

/** One field the apply wrote or deliberately left alone, with the values it decided between. */
export interface CatalogueReviewFieldApplied {
  kind: string;
  externalId: string;
  localRowId: number;
  field: string;
  previousValue: string | null;
  providerValue: string | null;
}

/**
 * What one apply did. `alreadyApplied` is the idempotent answer: a review that has been applied is
 * applied, so a second apply writes nothing at all and says so.
 */
export interface CatalogueReviewApplied {
  reviewId: number;
  alreadyApplied: boolean;
  appliedAtUtc: string;
  links: CatalogueReviewLinkApplied[];
  automaticChanges: CatalogueReviewFieldApplied[];
  acceptedChanges: CatalogueReviewFieldApplied[];
  skippedChanges: CatalogueReviewFieldApplied[];
  unresolvedSaleCount: number;
  createdCount: number;
  linkedCount: number;
}

/**
 * The provider catalogue review API (issue #738), which is how the product catalogue is imported
 * since issue #739: there is no direct catalogue import any more, so the operator's "Import
 * products" action starts a review here and applies their decisions.
 *
 * Both calls are `POST`, including the one that only previews: starting a review reads the
 * provider's live catalogue and records the review the apply is authorised by, so it must never be
 * served from a cache. `Imports.Run` is the capability behind both.
 *
 * The service decides nothing. Every provider name, group and link comes from the API, and the
 * apply carries decisions alone - no provider value is ever sent back from the browser.
 */
@Injectable({ providedIn: 'root' })
export class CatalogueReviewService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/catalogue-review`;
  }

  constructor(private readonly http: HttpClient, private readonly config: ConfigService) {}

  /** Reads the provider's catalogue and reports it against this business's own rows. */
  start(): Observable<CatalogueReview> {
    return this.http.post<CatalogueReview>(this.baseUrl, {});
  }

  /** Carries out exactly the submitted decisions against the named review, as one transaction. */
  apply(request: ApplyCatalogueReviewRequest): Observable<CatalogueReviewApplied> {
    return this.http.post<CatalogueReviewApplied>(`${this.baseUrl}/apply`, request);
  }
}
