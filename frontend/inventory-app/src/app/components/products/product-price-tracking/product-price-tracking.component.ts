import { Component, Input, OnChanges, OnDestroy, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Observable, of, Subject, Subscription } from 'rxjs';
import { catchError, map, switchMap, tap } from 'rxjs/operators';
import { ProductService } from '../../../services/product.service';
import { ProductPriceTracking, ProductPriceTrackingStatus } from '../../../models/models';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { RolePreviewService } from '../../../role-preview/role-preview.service';
import { apiErrorMessage } from '../../shared/api-error-message';

/** A read tagged with the product it was issued for; `state` is null when the read failed. */
type ProductPriceTrackingRead = { id: number; state: ProductPriceTracking | null };

/**
 * The "Track supplier prices" switch and the mapping status beside it (issue #938, fourth task of
 * #817). It owns its own read, its own write and their loading, refusal and error states, so the
 * product-edit page only has to supply the product identity (issue #191's page composition
 * boundary).
 *
 * It saves separately from the product form on purpose, like the GST rule panel above it. Tracking is
 * its own API resource: turning it on creates this business's statement that it wants the product
 * priced and copies the product's own name and unit label as the search text, and turning it off
 * removes that statement. Neither changes a name, a cost, a quantity or a price, and neither touches
 * the shared catalogue of master products and supplier listings.
 *
 * **It words no rule of its own.** Whether the switch may be turned on, and the sentence explaining
 * why not, are the server's answers (`canTrack`/`trackRefusal`, decided by
 * `Inventory.Domain.PriceCatalogue.ProductPriceTrackingPolicy`) - the same shape the product's
 * `canDelete`/`deletionRefusal` already has. What this component words is the five statuses, which
 * are presentation.
 */
@Component({
  selector: 'app-product-price-tracking',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './product-price-tracking.component.html'
})
export class ProductPriceTrackingComponent implements OnChanges, OnDestroy {
  @Input() productId: number | null = null;

  /** The state the server last reported, or `null` until a read has succeeded. */
  state: ProductPriceTracking | null = null;

  loading = false;
  saving = false;
  error = '';

  /**
   * Whether a support session is active (issue #554): the switch is disabled while it is, because a
   * session grants `InventoryOperate` - the capability this write is gated on - and the backend
   * refuses the write itself only by its read-only method rule, never by withholding the capability.
   */
  sessionActive = false;

  /**
   * Whether a Super Admin is previewing a role (issue #720), which is read-only for the same reason
   * and refused by its own method rule. Both are disabled rather than hidden here: the status beside
   * the switch is worth reading in either state, and a control that disappears gives no reason.
   */
  previewing = false;

  private readonly requestedProductId = new Subject<number | null>();
  private readonly loads: Subscription;
  private readonly sessionActiveSubscription: Subscription;
  private readonly previewSubscription: Subscription;

  constructor(
    private readonly productService: ProductService,
    supportSession: SupportSessionService,
    rolePreview: RolePreviewService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
    this.previewSubscription = rolePreview.active$.subscribe((active) => (this.previewing = active));
    // switchMap cancels the in-flight read whenever the product changes, so opening product A and
    // then B can never let A's slower response describe B. The identity check discards a response
    // that arrives regardless.
    this.loads = this.requestedProductId
      .pipe(
        tap(() => this.reset()),
        switchMap((id): Observable<ProductPriceTrackingRead | null> => {
          if (id === null) {
            return of(null);
          }

          this.loading = true;
          return this.productService.getPriceTracking(id).pipe(
            map((state) => ({ id, state })),
            catchError(() => of({ id, state: null }))
          );
        })
      )
      .subscribe((result) => {
        if (result === null || result.id !== this.productId) {
          return;
        }

        this.loading = false;
        if (result.state === null) {
          this.error =
            'Failed to load supplier price tracking. The switch stays unavailable until it loads; reload the page to try again.';
          return;
        }

        this.state = result.state;
      });
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['productId']) {
      this.requestedProductId.next(this.productId);
    }
  }

  ngOnDestroy(): void {
    this.loads.unsubscribe();
    this.sessionActiveSubscription.unsubscribe();
    this.previewSubscription.unsubscribe();
    this.requestedProductId.complete();
  }

  /** Whether the business currently has supplier prices tracked for this product. */
  get tracked(): boolean {
    return this.state?.tracked === true;
  }

  /**
   * Whether the switch is unavailable. It is while the state has not loaded, while a write is in
   * flight, in a read-only support session or role preview, and - for turning it *on* - while the
   * server says the product may not be tracked. Turning it **off** stays available in that last
   * case: a product that was tracked before its unit label was made generic must still be
   * untrackable, or the business would be stuck with a search it cannot withdraw.
   */
  get switchDisabled(): boolean {
    if (this.state === null || this.saving || this.sessionActive || this.previewing) {
      return true;
    }

    return !this.tracked && !this.state.canTrack;
  }

  /**
   * Why this product may not be tracked as it now stands, in the server's own words, or `null` when
   * there is nothing to explain.
   *
   * It is shown whenever the server reports one, tracking on or off. While tracking is off it is a
   * reason not to start; while it is on it is the reason the mapping is **paused** - a product whose
   * unit label was replaced by the generic one is not searched for and holds no match any more, and
   * this sentence is the only thing that says how to resume it (issue #938, repaired on review).
   */
  get refusal(): string | null {
    return this.state?.trackRefusal ?? null;
  }

  /**
   * What the mapping's status says, or `null` while the product is not tracked. The five states are
   * answers rather than labels on each other, so each has its own sentence: "not found" reads as the
   * recorded outcome it is rather than as an error, and a paused mapping says it is paused rather
   * than looking like one that is still waiting its turn. The paused line words no rule - the
   * server's own `trackRefusal` sentence below it is what explains the cause.
   */
  get statusLabel(): string | null {
    if (!this.tracked) {
      return null;
    }

    switch (this.state?.status) {
      case ProductPriceTrackingStatus.Requested:
        return 'Awaiting match';
      case ProductPriceTrackingStatus.AutoMatched:
        return 'Matched automatically, awaiting check';
      case ProductPriceTrackingStatus.Matched:
        return 'Matched';
      case ProductPriceTrackingStatus.NotFound:
        return 'Not found';
      case ProductPriceTrackingStatus.AwaitingProductDetails:
        return 'Paused - this product cannot be searched for';
      default:
        return null;
    }
  }

  /**
   * The matched master product as one line - its name and its size - or `null` while the mapping
   * names none. Those two values are the whole of what the API publishes about the shared catalogue.
   */
  get matchedDescription(): string | null {
    const master = this.state?.masterProduct;
    return master ? `${master.name} ${master.sizeValue}${master.sizeUnit}` : null;
  }

  /**
   * What is being searched for while the mapping waits, or `null` when there is nothing to search.
   *
   * A **paused** mapping shows nothing here, because nothing is being searched for: the text it
   * still holds is the product's previous name and size, and printing it under "Searched for as"
   * would say the opposite of what is happening.
   */
  get searchText(): string | null {
    if (
      !this.tracked
      || this.state?.searchName == null
      || this.state.status === ProductPriceTrackingStatus.AwaitingProductDetails
    ) {
      return null;
    }

    return `${this.state.searchName} ${this.state.unitLabel ?? ''}`.trim();
  }

  /**
   * The switch's change handler. What is stored - not what was clicked - is what the control shows:
   * the checkbox is put straight back to {@link tracked} and only moves when a re-read says the
   * server stored the change. Otherwise a refused or failed write would leave a switch saying the
   * opposite of what the business actually has, which is the one thing this control must never do.
   */
  toggle(event: Event): void {
    const control = event.target as HTMLInputElement;
    const requested = control.checked;
    control.checked = this.tracked;

    const id = this.productId;
    // Checked here as well as disabled in the template, so the write is actually unavailable rather
    // than merely inert - the endpoint refuses it either way.
    if (id === null || this.switchDisabled || requested === this.tracked) {
      return;
    }

    const tracked = requested;
    this.saving = true;
    this.error = '';
    this.productService.setPriceTracking(id, tracked).subscribe({
      next: () => {
        // A write that finishes after another product was opened reports nothing: its outcome
        // belongs to the product it was issued for, not to the switch now on screen.
        if (id !== this.productId) {
          return;
        }

        this.saving = false;
        // Re-read rather than assume: turning tracking on gives the mapping a status and a captured
        // search text the server decided, and this is the one place they are published.
        this.requestedProductId.next(id);
      },
      error: (error: unknown) => {
        if (id !== this.productId) {
          return;
        }

        this.saving = false;
        this.error = apiErrorMessage(
          error,
          tracked ? 'Failed to turn supplier price tracking on.' : 'Failed to turn supplier price tracking off.'
        );
      }
    });
  }

  private reset(): void {
    this.state = null;
    this.loading = false;
    this.saving = false;
    this.error = '';
  }
}
