import { CommonModule, DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subject, takeUntil } from 'rxjs';
import { ToastService } from '../../../services/toast.service';
import {
  MASTER_PRODUCT_SIZE_UNIT_LABELS,
  PriceCatalogueReviewQueue,
  ReviewAutomaticMatch,
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
