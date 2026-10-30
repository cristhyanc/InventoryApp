import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

/**
 * The unit a master product's individual size is measured in, as the API serializes it (backend
 * `Inventory.Domain.PriceCatalogue.MasterProductSizeUnit`, issue #923). The numbers are the
 * contract, so they must stay in step with that enum.
 */
export enum MasterProductSizeUnit {
  Millilitre = 1,
  Litre = 2,
  Gram = 3,
  Kilogram = 4,
  Each = 5
}

/** What a stored barcode is the barcode *of* (backend `BarcodeScope`, issue #923). */
export enum BarcodeScope {
  Unit = 1,
  Carton = 2
}

/** How confident the catalogue is that a listing is the product it hangs from (issue #923). */
export enum SupplierListingMatchStatus {
  Proposed = 1,
  Confirmed = 2,
  NeedsReview = 3,
  Rejected = 4
}

/** What has happened to one match suggestion (issue #923). */
export enum ProductMatchSuggestionStatus {
  Tentative = 1,
  AutoApplied = 2,
  Accepted = 3,
  Rejected = 4,
  Undone = 5,
  Repeated = 6
}

/** Whether an observed pack price includes GST, as observed (issue #925). */
export enum SupplierPriceBasis {
  ExclusiveOfGst = 1,
  InclusiveOfGst = 2
}

/** What the supplier said about availability when the price was read (issue #925). */
export enum SupplierStockStatus {
  InStock = 1,
  OutOfStock = 2,
  Unknown = 3
}

/** What a Super Admin decided about one supplier listing (issue #928). */
export enum SupplierListingReviewOutcome {
  Confirmed = 1,
  Rejected = 2
}

/** What a Super Admin decided about one waiting product (issue #928). */
export enum ProductMatchReviewDecision {
  AcceptSuggestion = 1,
  ChooseMasterProduct = 2,
  CreateMasterProduct = 3,
  RejectSuggestion = 4,
  MarkNotFound = 5
}

/** One master product as the review reports it. The brand is here because the reviewer is deciding which product a description is. */
export interface ReviewMasterProduct {
  readonly id: number;
  readonly brand: string;
  readonly name: string;
  readonly sizeValue: number;
  readonly sizeUnit: MasterProductSizeUnit;
}

/** One match suggestion: either an existing master product, or a description of a new one. */
export interface ReviewMatchSuggestion {
  readonly id: number;
  readonly masterProduct: ReviewMasterProduct | null;
  readonly suggestedBrand: string | null;
  readonly suggestedName: string | null;
  readonly suggestedSizeValue: number | null;
  readonly suggestedSizeUnit: MasterProductSizeUnit | null;
  readonly confidence: number;
  readonly reason: string;
  readonly collectorRun: string;
  readonly status: ProductMatchSuggestionStatus;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
}

/**
 * The reading one listing is holding pending (issue #937), with the server's own answers about
 * whether it could become a price today.
 *
 * `isFresh` and `needsPriceException` are deliberately the server's: the freshness limit belongs to
 * the supplier master record and the band belongs to the backend's own policy, so a page that worked
 * either out would be a second implementation of a rule that decides whether a price is published.
 */
export interface ReviewPendingObservation {
  readonly id: number;
  readonly priceCollectorRunId: number;
  readonly priceCollectorWorkItemId: number;
  readonly reason: string;
  readonly packPrice: number;
  readonly regularPackPrice: number | null;
  readonly currency: string;
  readonly priceBasis: SupplierPriceBasis;
  readonly supplierGstRate: number | null;
  readonly isPromotion: boolean;
  readonly promotionEndsAtUtc: string | null;
  readonly stockStatus: SupplierStockStatus;
  readonly observedAtUtc: string;
  readonly sourceReference: string | null;
  readonly sourceUrl: string | null;
  readonly variant: string | null;
  readonly sizeValue: number | null;
  readonly sizeUnit: MasterProductSizeUnit | null;
  readonly barcode: string | null;
  readonly barcodeScope: BarcodeScope | null;
  readonly unitsPerPack: number | null;
  readonly title: string | null;
  readonly recordedAtUtc: string;
  readonly isFresh: boolean;
  readonly needsPriceException: boolean;
}

/** The confirmed listing whose barcode this one carries: evidence, and never a decision. */
export interface ReviewBarcodeMatch {
  readonly supplierListingId: number;
  readonly supplierName: string;
  readonly masterProduct: ReviewMasterProduct | null;
}

/**
 * One supplier listing awaiting a decision. Every text value is the supplier's own wording and is
 * rendered as plain text; the only link is {@link url}.
 */
export interface ReviewSupplierListing {
  readonly id: number;
  readonly supplierPresetId: number;
  readonly supplierName: string;
  readonly supplierProductId: string;
  readonly url: string | null;
  readonly title: string | null;
  readonly variant: string | null;
  readonly sizeValue: number | null;
  readonly sizeUnit: MasterProductSizeUnit | null;
  readonly unitsPerPack: number | null;
  readonly barcode: string | null;
  readonly barcodeScope: BarcodeScope | null;
  readonly matchStatus: SupplierListingMatchStatus;
  readonly masterProduct: ReviewMasterProduct | null;
  readonly waitingKey: string | null;
  readonly originWaitingKey: string | null;
  readonly firstSeenAtUtc: string;
  readonly lastSeenAtUtc: string;
  readonly lastCheckedAtUtc: string | null;
  readonly lastNotFoundAtUtc: string | null;
  readonly lastAcceptedPackPrice: number | null;
  readonly strongBarcodeMatch: ReviewBarcodeMatch | null;
  readonly pendingObservation: ReviewPendingObservation | null;
}

/**
 * One product awaiting a match, by its search name and unit label - and a **count** of the
 * businesses waiting. Which business asked is not part of this contract and there is no field for it.
 */
export interface ReviewWaitingProduct {
  readonly waitingKey: string;
  readonly searchName: string;
  readonly unitLabel: string;
  readonly businessesWaitingCount: number;
  readonly firstRequestedAtUtc: string;
  readonly lastUpdatedAtUtc: string;
  readonly suggestions: readonly ReviewMatchSuggestion[];
  readonly proposedListings: readonly ReviewSupplierListing[];
}

/** One automatic match (issue #941) waiting to be checked. */
export interface ReviewAutomaticMatch {
  readonly suggestionId: number;
  readonly waitingKey: string;
  readonly searchName: string | null;
  readonly unitLabel: string | null;
  readonly masterProduct: ReviewMasterProduct;
  readonly confidence: number;
  readonly reason: string;
  readonly collectorRun: string;
  readonly appliedAtUtc: string;
}

/**
 * The whole review queue, in the three sections the page is built from, with the shared master
 * catalogue the "pick another master product" choice is made from.
 */
export interface PriceCatalogueReviewQueue {
  readonly waitingProducts: readonly ReviewWaitingProduct[];
  readonly automaticMatches: readonly ReviewAutomaticMatch[];
  readonly listings: readonly ReviewSupplierListing[];
  readonly masterProducts: readonly ReviewMasterProduct[];
}

/**
 * What a listing decision submits. "Correct and confirm" is this same request with the corrected
 * values: the correction and the decision are one operation.
 */
export interface SupplierListingDecisionRequest {
  readonly outcome: SupplierListingReviewOutcome;
  readonly unitsPerPack: number | null;
  readonly barcode: string | null;
  readonly barcodeScope: BarcodeScope | null;
  readonly variant: string | null;
  readonly sizeValue: number | null;
  readonly sizeUnit: MasterProductSizeUnit | null;
  /** Whether a pending price outside the band may be accepted anyway. Audited separately by the server. */
  readonly approvePriceException: boolean;
}

/** What became of one listing decision, including what happened to the reading it was holding. */
export interface SupplierListingDecisionResult {
  readonly supplierListingId: number;
  readonly outcome: SupplierListingReviewOutcome;
  readonly matchStatus: SupplierListingMatchStatus;
  readonly priceAccepted: boolean;
  readonly priceExceptionUsed: boolean;
  readonly priceReason: string | null;
  readonly acceptedObservationId: number | null;
  readonly acceptedPackPrice: number | null;
  readonly confirmedUnitsPerPack: number | null;
}

/** What a match decision submits: which decision, and the one thing it needs. */
export interface ProductMatchDecisionRequest {
  readonly waitingKey: string;
  readonly decision: ProductMatchReviewDecision;
  readonly suggestionId: number | null;
  readonly masterProductId: number | null;
  readonly newBrand: string | null;
  readonly newName: string | null;
  readonly newSizeValue: number | null;
  readonly newSizeUnit: MasterProductSizeUnit | null;
}

/** What became of one match decision. */
export interface ProductMatchDecisionResult {
  readonly waitingKey: string;
  readonly decision: ProductMatchReviewDecision;
  readonly masterProduct: ReviewMasterProduct | null;
  readonly masterProductCreated: boolean;
  readonly suggestionId: number | null;
  readonly listingsMovedCount: number;
}

/** What became of one automatic-match decision. */
export interface AutomaticMatchDecisionResult {
  readonly suggestionId: number;
  readonly decision: number;
  readonly waitingKey: string;
  readonly masterProduct: ReviewMasterProduct;
  readonly listingsMovedCount: number;
}

/** What each size unit is called on screen, in the server's own codes (issue #923). */
export const MASTER_PRODUCT_SIZE_UNIT_LABELS: Readonly<Record<MasterProductSizeUnit, string>> = {
  [MasterProductSizeUnit.Millilitre]: 'ml',
  [MasterProductSizeUnit.Litre]: 'l',
  [MasterProductSizeUnit.Gram]: 'g',
  [MasterProductSizeUnit.Kilogram]: 'kg',
  [MasterProductSizeUnit.Each]: 'each'
};

/** What each barcode scope is called on screen. */
export const BARCODE_SCOPE_LABELS: Readonly<Record<BarcodeScope, string>> = {
  [BarcodeScope.Unit]: 'Unit',
  [BarcodeScope.Carton]: 'Carton'
};

/** What each listing match status is called on screen. */
export const LISTING_MATCH_STATUS_LABELS: Readonly<Record<SupplierListingMatchStatus, string>> = {
  [SupplierListingMatchStatus.Proposed]: 'Proposed',
  [SupplierListingMatchStatus.Confirmed]: 'Confirmed',
  [SupplierListingMatchStatus.NeedsReview]: 'Needs review',
  [SupplierListingMatchStatus.Rejected]: 'Refused'
};

/**
 * The frontend client for `/api/super-admin/price-catalogue-review` (issue #928), consumed by the
 * Super Admin price review page.
 *
 * It is a thin wrapper over the five endpoints: no freshness limit, price band, identity rule or
 * match condition is decided here, only what the server already answered. That is the point of the
 * surface - a supplier listing is shared by every business, so what becomes a price is the backend's
 * decision and a page that computed any of it would be a second answer.
 */
@Injectable({ providedIn: 'root' })
export class SuperAdminPriceCatalogueReviewService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  /** The whole queue. No filter, page or sort parameter exists. */
  queue(): Observable<PriceCatalogueReviewQueue> {
    return this.http.get<PriceCatalogueReviewQueue>(this.baseUrl);
  }

  decideListing(
    supplierListingId: number,
    request: SupplierListingDecisionRequest
  ): Observable<SupplierListingDecisionResult> {
    return this.http.post<SupplierListingDecisionResult>(
      `${this.baseUrl}/listings/${supplierListingId}/decision`,
      request
    );
  }

  decideMatch(request: ProductMatchDecisionRequest): Observable<ProductMatchDecisionResult> {
    return this.http.post<ProductMatchDecisionResult>(`${this.baseUrl}/matches`, request);
  }

  confirmAutomaticMatch(suggestionId: number): Observable<AutomaticMatchDecisionResult> {
    return this.http.post<AutomaticMatchDecisionResult>(
      `${this.baseUrl}/automatic-matches/${suggestionId}/confirm`,
      null
    );
  }

  undoAutomaticMatch(suggestionId: number): Observable<AutomaticMatchDecisionResult> {
    return this.http.post<AutomaticMatchDecisionResult>(
      `${this.baseUrl}/automatic-matches/${suggestionId}/undo`,
      null
    );
  }

  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/super-admin/price-catalogue-review`;
  }
}
