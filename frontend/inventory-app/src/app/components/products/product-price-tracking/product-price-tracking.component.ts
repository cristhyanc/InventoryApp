import { Component, Input, OnChanges, OnDestroy, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Observable, of, Subject, Subscription, throwError } from 'rxjs';
import { catchError, map, switchMap, tap } from 'rxjs/operators';
import { ProductService } from '../../../services/product.service';
import { ProductPriceTracking, ProductPriceTrackingStatus } from '../../../models/models';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { RolePreviewService } from '../../../role-preview/role-preview.service';

/** A read tagged with the product it was issued for; `state` is null when the read failed. */
type ProductPriceTrackingRead = { id: number; state: ProductPriceTracking | null };

/**
 * The "Track supplier prices" switch and the mapping status beside it (issue #938, fourth task of
 * #817). It owns its own read, its own write and their loading, refusal and error states, so the
 * product-edit page only has to supply the product identity (issue #191's page composition
 * boundary).
 *
 * Since issue #1111 the switch **writes nothing when it is pressed**. It records the requested
 * setting ({@link requested}), and the product page's one primary action (Save changes) asks whether
 * it changed ({@link dirty}) and writes it ({@link persist}) after the product's own fields and its
 * GST rule - last, because turning tracking on copies the *stored* name and unit label as the search
 * text. Tracking is still its own API resource: turning it on creates this business's statement that
 * it wants the product priced, and turning it off removes that statement. Neither changes a name, a
 * cost, a quantity or a price, and neither touches the shared catalogue of master products and
 * supplier listings.
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

  /**
   * The setting the operator asked for and has not saved yet (issue #1111), or `null` while the
   * switch shows what is stored.
   */
  requested: boolean | null = null;

  /** Whether the page is saving (issue #1111); the switch is held still meanwhile. */
  @Input() disabled = false;

  loading = false;
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
            'Failed to load supplier price tracking, so it cannot be changed here. Other changes still save; reload the page to try again.';
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

  /** What the switch shows: the unsaved request when there is one, otherwise what is stored. */
  get checked(): boolean {
    return this.requested ?? this.tracked;
  }

  /** Whether the switch asks for something other than what is stored (issue #1111). */
  get dirty(): boolean {
    return this.state !== null && this.requested !== null && this.requested !== this.tracked;
  }

  /**
   * Whether the switch is unavailable. It is while the state has not loaded, while the page is
   * saving, in a read-only support session or role preview, and - for turning it *on* - while the
   * server says the product may not be tracked. Turning it **off** stays available in that last
   * case: a product that was tracked before its unit label was made generic must still be
   * untrackable, or the business would be stuck with a search it cannot withdraw.
   */
  get switchDisabled(): boolean {
    if (this.state === null || this.disabled || this.sessionActive || this.previewing) {
      return true;
    }

    return !this.tracked && !this.state.canTrack;
  }

  /** The sentence beside an unsaved switch, saying what Save changes will do, or `null`. */
  get pendingNote(): string | null {
    if (!this.dirty) {
      return null;
    }

    return this.requested
      ? 'Tracking starts when you save. The product is then searched for by its saved name and unit label.'
      : 'Tracking stops when you save.';
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
   * The colour family of the status pill: presentation only, and always beside the status text, so
   * the colour never carries the meaning on its own.
   */
  get statusTone(): 'ok' | 'teal' | 'warn' | 'neutral' {
    switch (this.state?.status) {
      case ProductPriceTrackingStatus.Matched:
        return 'ok';
      case ProductPriceTrackingStatus.AutoMatched:
        return 'teal';
      case ProductPriceTrackingStatus.NotFound:
      case ProductPriceTrackingStatus.AwaitingProductDetails:
        return 'warn';
      default:
        return 'neutral';
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
   * The switch's change handler (issue #1111): it records the request and writes nothing. A switch
   * that may not move - not loaded, saving, read-only, or a refused turn-on - is put straight back to
   * what it showed, so the control never says something the page would not save.
   */
  toggle(event: Event): void {
    const control = event.target as HTMLInputElement;
    const requested = control.checked;

    if (this.state === null || this.switchDisabled) {
      control.checked = this.checked;
      return;
    }

    this.requested = requested === this.tracked ? null : requested;
  }

  /**
   * Writes the requested setting when it differs from what is stored (issue #1111), for the page's
   * one Save changes. An unchanged switch writes nothing and completes at once. A failure is the
   * caller's to report, and leaves the request in place, so pressing Save changes again retries it.
   *
   * After a successful write the state is read again rather than assumed: turning tracking on gives
   * the mapping a status and a captured search text the server decided, and this is the one place
   * they are published.
   */
  persist(): Observable<void> {
    const id = this.productId;
    const requested = this.requested;
    if (!this.dirty || id === null || requested === null) {
      return of(undefined);
    }
    // Checked here as well as on the page, so the write is actually unavailable rather than merely
    // inert - the endpoint refuses it either way.
    if (this.sessionActive || this.previewing) {
      return throwError(() => new Error('Supplier price tracking cannot be changed in a read-only session.'));
    }

    return this.productService.setPriceTracking(id, requested).pipe(
      tap(() => {
        // A write that finishes after another product was opened reports nothing: its outcome
        // belongs to the product it was issued for, not to the switch now on screen.
        if (id === this.productId) {
          this.requestedProductId.next(id);
        }
      })
    );
  }

  private reset(): void {
    this.state = null;
    this.requested = null;
    this.loading = false;
    this.error = '';
  }
}
