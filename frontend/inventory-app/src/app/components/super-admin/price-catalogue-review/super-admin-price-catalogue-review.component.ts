import { CommonModule, DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit, QueryList, ViewChildren } from '@angular/core';
import { Subject, takeUntil } from 'rxjs';
import { ToastService } from '../../../services/toast.service';
import {
  MASTER_PRODUCT_SIZE_UNIT_LABELS,
  PriceCatalogueReviewQueue,
  ReviewAutomaticMatch,
  ReviewSupplierListing,
  SuperAdminPriceCatalogueReviewService
} from '../../../services/super-admin-price-catalogue-review.service';
import { ConfirmationDialogComponent } from '../../shared/confirmation-dialog.component';
import { SupplierListingReviewComponent } from './supplier-listing-review.component';
import { WaitingProductMatchComponent } from './waiting-product-match.component';
import { extractReviewErrorMessage, isStaleReviewState } from './review-error';

/**
 * The Super Admin price review page (issue #928, parent #817), at
 * `/super-admin/price-catalogue-review`, over the five endpoints of the price-catalogue review API.
 *
 * It is the one place a person decides anything about the **shared** supplier price catalogue: which
 * master product a waiting search was for, whether a collector run's automatic match was right, and
 * whether a supplier's listing really is the pack it claims to be. A Business Owner reaches none of
 * it, because the data is not theirs - one wrong pack quantity is one wrong "cheapest supplier" for
 * every business tracking the product.
 *
 * Per docs/architecture.md § Page composition boundary the page is composition and orchestration: it
 * owns the queue read, its loading and error states, and the confirmation behind the shared
 * `ConfirmationDialogComponent` for an undo, while the two workflows with forms of their own - a
 * waiting product's match and a listing's confirmation - are dedicated feature components composed
 * through `[product]`/`[listing]` and their `(decided)` outputs.
 *
 * **It decides nothing itself.** The freshness limit, the price band, the identity comparison and
 * which mappings a match reaches are the server's rules and its answers; this page shows them and
 * re-reads the queue after every decision, so what a reviewer sees is what is stored rather than what
 * was last clicked.
 *
 * **It never learns which businesses are waiting.** The API publishes a count and no identity, so
 * there is nothing here to render even by accident.
 */
@Component({
  selector: 'app-super-admin-price-catalogue-review',
  standalone: true,
  imports: [
    CommonModule,
    DatePipe,
    ConfirmationDialogComponent,
    SupplierListingReviewComponent,
    WaitingProductMatchComponent
  ],
  templateUrl: './super-admin-price-catalogue-review.component.html'
})
export class SuperAdminPriceCatalogueReviewComponent implements OnInit, OnDestroy {
  queue: PriceCatalogueReviewQueue | null = null;
  loading = false;
  loadError = false;

  /** The automatic match an undo confirmation is pending for. */
  undoTarget: ReviewAutomaticMatch | null = null;

  /** Which automatic match is being decided, so its two buttons can be disabled while it is. */
  decidingSuggestionId: number | null = null;

  /**
   * The ids of the supplier listings a "Confirm selected" run would confirm (issue #1055), checked
   * on their own card rather than this page's. Pruned to the queue's own listing ids on every read,
   * so a stale id never inflates "Confirm selected (N)" after a listing has left the section.
   */
  selectedListingIds = new Set<number>();

  /** Whether a "Confirm selected" run is sequencing its requests, one listing at a time. */
  bulkConfirmInProgress = false;

  /** Which listing of the run is in flight and how many there are in total, while one is running. */
  bulkConfirmProgress: { current: number; total: number } | null = null;

  /**
   * The server's own refusal reason for a listing the last "Confirm selected" run could not confirm,
   * kept here rather than on the card (issue #1055) because the one queue re-read after the whole
   * run replaces every card's `listing` input and would otherwise clear it through `ngOnChanges`.
   */
  bulkConfirmErrorsByListingId: Record<number, string> = {};

  /** Whether the "Confirm N supplier listings?" dialog is open. */
  confirmSelectedRequested = false;

  @ViewChildren(SupplierListingReviewComponent)
  private readonly listingCards?: QueryList<SupplierListingReviewComponent>;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly reviewService: SuperAdminPriceCatalogueReviewService,
    private readonly toast: ToastService
  ) {}

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  load(): void {
    this.loading = true;
    this.loadError = false;

    this.reviewService
      .queue()
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (queue) => {
          this.queue = queue;
          this.loading = false;
          this.pruneBulkConfirmStateToQueue(queue);
        },
        error: () => {
          this.loading = false;
          this.loadError = true;
        }
      });
  }

  /** Whether there is nothing at all to review, which is a real and expected state. */
  get isEmpty(): boolean {
    return (
      this.queue !== null &&
      this.queue.waitingProducts.length === 0 &&
      this.queue.automaticMatches.length === 0 &&
      this.queue.listings.length === 0
    );
  }

  /** What an automatic match's master product is called, in words. */
  describe(match: ReviewAutomaticMatch): string {
    const master = match.masterProduct;

    return `${master.brand} ${master.name} ${master.sizeValue}${MASTER_PRODUCT_SIZE_UNIT_LABELS[master.sizeUnit]}`;
  }

  /** The matcher's confidence as a percentage, which is how a proportion reads to a person. */
  confidencePercent(confidence: number): number {
    return Math.round(confidence * 100);
  }

  /**
   * What an automatic match was a match <em>of</em>: the search text of the mappings it applied to,
   * or - when a business has turned tracking off since - nothing but its own key, which is said
   * rather than invented.
   */
  searchedFor(match: ReviewAutomaticMatch): string {
    return match.searchName === null
      ? 'the search this key folds from'
      : `${match.searchName} · ${match.unitLabel}`;
  }

  onDecided(message: string): void {
    this.toast.success(message);
    this.load();
  }

  /** A refusal that says the page is stale re-reads the queue; the refusal itself stays on screen. */
  onStaleState(): void {
    this.load();
  }

  isListingSelected(listingId: number): boolean {
    return this.selectedListingIds.has(listingId);
  }

  toggleListingSelected(listingId: number, selected: boolean): void {
    if (selected) {
      this.selectedListingIds.add(listingId);
    } else {
      this.selectedListingIds.delete(listingId);
    }
  }

  /** Every listing currently shown is selected, none is, or - the indeterminate state - some are. */
  get allListingsSelected(): boolean {
    const listings = this.queue?.listings ?? [];

    return listings.length > 0 && listings.every((listing) => this.selectedListingIds.has(listing.id));
  }

  get someListingsSelected(): boolean {
    return this.selectedListingIds.size > 0 && !this.allListingsSelected;
  }

  toggleSelectAllListings(selected: boolean): void {
    if (!this.queue) {
      return;
    }

    if (selected) {
      this.queue.listings.forEach((listing) => this.selectedListingIds.add(listing.id));
    } else {
      this.selectedListingIds.clear();
    }
  }

  bulkConfirmErrorFor(listingId: number): string | null {
    return this.bulkConfirmErrorsByListingId[listingId] ?? null;
  }

  get confirmSelectedDisabled(): boolean {
    return this.selectedListingIds.size === 0 || this.bulkConfirmInProgress;
  }

  get confirmSelectedButtonLabel(): string {
    return this.bulkConfirmInProgress && this.bulkConfirmProgress
      ? `Confirming ${this.bulkConfirmProgress.current} of ${this.bulkConfirmProgress.total}…`
      : `Confirm selected (${this.selectedListingIds.size})`;
  }

  requestConfirmSelected(): void {
    this.confirmSelectedRequested = true;
  }

  cancelConfirmSelected(): void {
    this.confirmSelectedRequested = false;
  }

  get confirmSelectedTitle(): string {
    return `Confirm ${this.selectedListingIds.size} supplier listings?`;
  }

  get confirmSelectedMessage(): string {
    return (
      'Each selected listing is confirmed with the values currently shown on its own card, including ' +
      'any edits made there, and none of them will approve a price exception.'
    );
  }

  /**
   * Confirms every selected listing through the same per-listing decision endpoint
   * `SupplierListingReviewComponent.confirm()` uses, one request at a time rather than in parallel,
   * and with `approvePriceException` forced to `false` for every one of them (issue #1055). One
   * failure does not stop the rest; the queue is re-read once, after the whole run, rather than
   * after each listing.
   */
  confirmSelected(): void {
    this.confirmSelectedRequested = false;

    const queue = this.queue;

    if (!queue) {
      return;
    }

    const targets = queue.listings.filter((listing) => this.selectedListingIds.has(listing.id));

    if (targets.length === 0) {
      return;
    }

    this.bulkConfirmInProgress = true;
    this.bulkConfirmErrorsByListingId = {};
    this.confirmNextSelected(targets, 0, new Set<number>());
  }

  private confirmNextSelected(
    targets: readonly ReviewSupplierListing[],
    index: number,
    failedListingIds: Set<number>
  ): void {
    if (index >= targets.length) {
      this.finishConfirmSelected(targets.length, failedListingIds);
      return;
    }

    this.bulkConfirmProgress = { current: index + 1, total: targets.length };

    const target = targets[index];
    const card = this.listingCards?.find((listingCard) => listingCard.listing.id === target.id);
    const request = card!.buildConfirmationRequest();

    this.reviewService
      .decideListing(target.id, request)
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: () => this.confirmNextSelected(targets, index + 1, failedListingIds),
        error: (err: unknown) => {
          failedListingIds.add(target.id);
          this.bulkConfirmErrorsByListingId[target.id] = extractReviewErrorMessage(
            err,
            'Unable to record this decision.'
          );
          this.confirmNextSelected(targets, index + 1, failedListingIds);
        }
      });
  }

  private finishConfirmSelected(total: number, failedListingIds: Set<number>): void {
    this.bulkConfirmInProgress = false;
    this.bulkConfirmProgress = null;

    const confirmedCount = total - failedListingIds.size;
    this.selectedListingIds = failedListingIds;

    if (failedListingIds.size === 0) {
      this.toast.success(`${confirmedCount} ${confirmedCount === 1 ? 'listing' : 'listings'} confirmed.`);
    } else {
      this.toast.warning(`${confirmedCount} confirmed, ${failedListingIds.size} not confirmed.`);
    }

    this.load();
  }

  /** Drops a selected or failed listing id the moment it is no longer in the queue read back. */
  private pruneBulkConfirmStateToQueue(queue: PriceCatalogueReviewQueue): void {
    const presentIds = new Set(queue.listings.map((listing) => listing.id));

    this.selectedListingIds = new Set([...this.selectedListingIds].filter((id) => presentIds.has(id)));

    this.bulkConfirmErrorsByListingId = Object.fromEntries(
      Object.entries(this.bulkConfirmErrorsByListingId).filter(([id]) => presentIds.has(Number(id)))
    );
  }

  confirmAutomaticMatch(match: ReviewAutomaticMatch): void {
    this.decidingSuggestionId = match.suggestionId;

    this.reviewService
      .confirmAutomaticMatch(match.suggestionId)
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: () => {
          this.decidingSuggestionId = null;
          this.onDecided(`${this.describe(match)} is confirmed as the match for this search.`);
        },
        error: (err: unknown) => this.onAutomaticMatchFailed(err)
      });
  }

  requestUndo(match: ReviewAutomaticMatch): void {
    this.undoTarget = match;
  }

  cancelUndo(): void {
    this.undoTarget = null;
  }

  get undoTitle(): string {
    return 'Undo automatic match';
  }

  get undoMessage(): string {
    const target = this.undoTarget;

    if (!target) {
      return '';
    }

    return (
      `Undo the automatic match to ${this.describe(target)}? The businesses waiting under this search ` +
      'go back to awaiting a match, the proposed supplier listings move back to it, confirmed listings ' +
      'are left as they are, and this pair is never matched automatically again.'
    );
  }

  confirmUndo(): void {
    const target = this.undoTarget;

    if (!target) {
      return;
    }

    this.undoTarget = null;
    this.decidingSuggestionId = target.suggestionId;

    this.reviewService
      .undoAutomaticMatch(target.suggestionId)
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (result) => {
          this.decidingSuggestionId = null;
          this.onDecided(
            `The automatic match to ${this.describe(target)} was undone, and ${result.listingsMovedCount} ` +
              `supplier ${result.listingsMovedCount === 1 ? 'listing' : 'listings'} moved back to the search.`
          );
        },
        error: (err: unknown) => this.onAutomaticMatchFailed(err)
      });
  }

  private onAutomaticMatchFailed(err: unknown): void {
    this.decidingSuggestionId = null;
    this.toast.error(extractReviewErrorMessage(err, 'Unable to record this decision.'));

    if (isStaleReviewState(err)) {
      this.load();
    }
  }
}
