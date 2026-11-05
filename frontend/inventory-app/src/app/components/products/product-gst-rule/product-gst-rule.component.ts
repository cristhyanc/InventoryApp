import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Observable, of, Subject, Subscription, throwError } from 'rxjs';
import { catchError, map, switchMap, tap } from 'rxjs/operators';
import { ProductService } from '../../../services/product.service';
import { GstClassification, ProductGstRule } from '../../../models/models';
import { SupportSessionService } from '../../../support-session/support-session.service';

/** A read tagged with the product it was issued for; `configured` is null when the read failed. */
type ProductGstRuleRead = { id: number; configured: ProductGstRule | null };

/**
 * Product GST rule configuration (issue #430). It owns its own read, loading and load-error state,
 * so the product-edit page only has to supply the product identity.
 *
 * Since issue #1111 it has **no Save button of its own**. The product page's one primary action
 * (Save changes) saves every editable section, so the page asks this panel whether the rule was
 * changed ({@link dirty}) and, when it was, to write it ({@link persist}) after the product's own
 * fields. The rule is still its own API resource: writing it changes no name, price, cost or stock,
 * and it never reclassifies a purchase that is already recorded. Historical classification is the
 * Admin Preview/Apply maintenance workflow (issue #433), not this page.
 *
 * On New Product it runs **deferred** (issue #829): there is no product to read a rule from or write
 * one to yet, so the panel only chooses the rule and reports it, and `ProductCreateGstRuleComponent`
 * - which composes it there - writes it to `PUT api/products/{id}/gst-rule` once the create has
 * answered an id. Everything else - the vocabulary, the labels, the help and the rule's meaning - is
 * the same in both modes, because the same configuration is being made.
 */
@Component({
  selector: 'app-product-gst-rule',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './product-gst-rule.component.html'
})
export class ProductGstRuleComponent implements OnChanges, OnDestroy {
  @Input() productId: number | null = null;

  /**
   * Whether the product does not exist yet (issue #829), which is New Product and nothing else.
   *
   * Deferred, the panel reads no rule and saves none: a product that has not been created has no
   * stored rule to read, and `Unknown` - the picker's own starting value - is exactly the rule a
   * created product carries, so the placeholder states the truth here instead of threatening to
   * erase a configured rule. The composing component owns the write, because only it knows when
   * there is an id.
   */
  @Input() deferred = false;

  /**
   * Whether the page is saving (issue #1111): the picker is held still while a save is in flight, so
   * the rule written is the rule that was on screen when Save changes was pressed.
   */
  @Input() disabled = false;

  /** The chosen rule, for the component that persists it once the product exists (issue #829). */
  @Output() readonly ruleChange = new EventEmitter<GstClassification>();

  /** `Unknown` is "no rule", a state of its own; it is never the same as an explicit GST-free rule. */
  readonly options: { value: GstClassification; label: string }[] = [
    { value: GstClassification.Unknown, label: 'No rule' },
    { value: GstClassification.Taxable, label: 'Taxable' },
    { value: GstClassification.GstFree, label: 'GST-free' }
  ];

  rule: GstClassification = GstClassification.Unknown;

  /**
   * The rule the server last confirmed for the product on screen - read back, or just written - so
   * {@link dirty} compares the picker with what is actually stored rather than with what was read
   * when the page opened.
   */
  storedRule: GstClassification = GstClassification.Unknown;

  /**
   * False until the stored rule of the product currently on screen has been read back successfully.
   * The picker starts on the "no rule" placeholder, so writing before that - in particular after a
   * failed load - would erase the rule the product already has. The picker stays disabled, and the
   * rule is never {@link dirty}, while this is false.
   */
  loaded = false;

  loading = false;
  error = '';

  /**
   * Whether a support session is active (issue #661): the rule is never written while it is,
   * because a session grants `InventoryOperate` - the capability the product pages and this rule
   * resource are gated on - and the backend refuses the write itself only by its read-only method
   * rule, never by withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  private readonly requestedProductId = new Subject<number | null>();
  private readonly loads: Subscription;
  private readonly sessionActiveSubscription: Subscription;

  constructor(private productService: ProductService, supportSession: SupportSessionService) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
    // switchMap cancels the in-flight read whenever the product changes, so opening product A and
    // then B can never let A's slower response populate B's picker - and a later save write A's
    // value onto B. The identity check discards a response that arrives regardless.
    this.loads = this.requestedProductId
      .pipe(
        tap(() => this.reset()),
        switchMap((id): Observable<ProductGstRuleRead | null> => {
          if (id === null) {
            return of(null);
          }

          this.loading = true;
          return this.productService.getGstRule(id).pipe(
            map((configured) => ({ id, configured })),
            catchError(() => of({ id, configured: null }))
          );
        })
      )
      .subscribe((result) => {
        if (result === null || result.id !== this.productId) {
          return;
        }

        this.loading = false;
        if (result.configured === null) {
          this.error =
            'Failed to load the GST rule, so it cannot be changed here. Other changes still save; reload the page to try again.';
          return;
        }

        this.rule = result.configured.gstRule;
        this.storedRule = result.configured.gstRule;
        this.loaded = true;
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
    this.requestedProductId.complete();
  }

  /**
   * The picker's change handler, which also reports the selection (issue #829). One code path for
   * both modes: the edit page binds no output, so nothing there changes.
   */
  selectRule(rule: GstClassification): void {
    this.rule = rule;
    this.ruleChange.emit(rule);
  }

  /**
   * Whether the picker differs from the stored rule (issue #1111), which is what tells the page that
   * Save changes has a GST write to make. Never true before the stored rule has been read back, and
   * never true deferred - on New Product the create's own flow writes the rule.
   */
  get dirty(): boolean {
    return !this.deferred && this.loaded && this.rule !== this.storedRule;
  }

  /**
   * Writes the rule on screen when it was changed (issue #1111), for the page's one Save changes.
   * An unchanged rule writes nothing and completes at once. A failure is the caller's to report -
   * the page says which of its sections were and were not saved - and leaves the picker, and
   * {@link dirty}, exactly as they were, so pressing Save changes again retries this write.
   *
   * The session check is here as well as on the page, so the write is actually unavailable rather
   * than merely inert - the endpoint refuses it either way.
   */
  persist(): Observable<void> {
    const id = this.productId;
    if (!this.dirty || id === null) {
      return of(undefined);
    }
    if (this.sessionActive) {
      return throwError(() => new Error('The GST rule cannot be changed during a support session.'));
    }

    const rule = this.rule;
    return this.productService.setGstRule(id, rule).pipe(
      tap(() => {
        // A write that finishes after another product was opened changes nothing on screen: its
        // outcome belongs to the product it was issued for.
        if (id === this.productId) {
          this.storedRule = rule;
        }
      })
    );
  }

  private reset(): void {
    this.rule = GstClassification.Unknown;
    this.storedRule = GstClassification.Unknown;
    this.loaded = false;
    this.loading = false;
    this.error = '';
  }
}
