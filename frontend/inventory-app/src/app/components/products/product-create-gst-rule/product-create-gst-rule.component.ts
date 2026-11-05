import { Component, EventEmitter, Input, OnDestroy, Output } from '@angular/core';
import { Subscription } from 'rxjs';
import { ProductService } from '../../../services/product.service';
import { GstClassification } from '../../../models/models';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { ProductGstRuleComponent } from '../product-gst-rule/product-gst-rule.component';

/**
 * The GST rule of a product being created (issue #829): choosing it, storing it on the product's own
 * GST resource once the create has answered an id, and retrying that one write when it fails.
 *
 * It exists because that is a workflow of its own - its own choice, its own `PUT`, its own
 * saving/error state and its own retry - and the routed `ProductFormComponent` is a composition
 * boundary rather than the place for a second write's lifecycle (docs/architecture.md
 * § Page composition boundary (issue #191)). The page creates the product and says which product now
 * exists by calling {@link storeFor} with the id the create answered; everything after that is this
 * component's, and it reports the one thing the page needs back: whether the rule is stored.
 *
 * The page calls it from the create's own response rather than through an input binding (issue
 * #1111): the outcome changes the page's action bar, and reporting it from inside a change-detection
 * pass - which is when an input change is seen - would change what that pass has already rendered.
 *
 * Since issue #1111 it offers no button of its own either: when the rule write fails, the page's one
 * primary action becomes "Retry GST rule" and calls {@link save}, which writes the rule alone.
 *
 * It does not choose the rule itself either: `ProductGstRuleComponent` runs **deferred** inside it,
 * so the vocabulary, the labels, the help and the rule's meaning are the edit page's, unchanged.
 * Deferred, that panel reads nothing and writes nothing - which is also why the chosen rule survives
 * a failed write here: nothing re-reads the product's stored (still unset) rule over it.
 */
@Component({
  selector: 'app-product-create-gst-rule',
  standalone: true,
  imports: [ProductGstRuleComponent],
  templateUrl: './product-create-gst-rule.component.html'
})
export class ProductCreateGstRuleComponent implements OnDestroy {
  /**
   * The id `POST api/products` answered, or `null` while no product exists yet. It is set by
   * {@link storeFor}, the first moment there is a product to store the rule on; the page keeps its
   * own copy, because the page is what created the product.
   */
  createdProductId: number | null = null;

  /**
   * The rule is stored - or needed no write at all - so the create is finished and the page may
   * leave. Nothing else reports a completed create (issue #829).
   */
  @Output() readonly persisted = new EventEmitter<void>();

  /**
   * The product exists and its rule is not stored. The page must not report a finished create; this
   * component says so on screen and offers the retry of this write alone.
   */
  @Output() readonly failed = new EventEmitter<void>();

  /** Each rule chosen in the composed panel, so the page can tell a changed form (issue #1111). */
  @Output() readonly ruleChange = new EventEmitter<GstClassification>();

  /** Whether the page is saving (issue #1111); the composed picker is held still meanwhile. */
  @Input() disabled = false;

  /** The rule chosen in the composed panel. `Unknown` - "no rule" - is what a new product carries. */
  rule: GstClassification = GstClassification.Unknown;

  saving = false;
  error = '';

  /**
   * Whether a support session is active (issue #661/#554): the retry is disabled and refused while
   * it is, because a session grants `InventoryOperate` - the capability this resource is gated on -
   * and the backend refuses the write itself only by its read-only method rule.
   */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private readonly productService: ProductService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  /**
   * The product now exists (issue #829): store the chosen rule on it. Answers whether the outcome
   * will be reported - it is not while a support session makes the write unavailable.
   */
  storeFor(id: number): boolean {
    this.createdProductId = id;
    return this.save();
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  /** The composed panel's choice, kept here and reported to the page. */
  onRuleChange(rule: GstClassification): void {
    this.rule = rule;
    this.ruleChange.emit(rule);
  }

  /**
   * Retries the rule write after a failure, with the rule on screen now, so a corrected choice is
   * the one stored. It writes the rule and nothing else: the product already exists, and creating a
   * second one is exactly what this retry must never do.
   *
   * Answers whether a write (or the no-write "no rule" outcome) was started, so the page - whose
   * primary action calls this (issue #1111) - never waits on an outcome that will not be reported.
   */
  save(): boolean {
    const id = this.createdProductId;
    // Checked here as well as on the page, so the write is actually unavailable rather than merely
    // inert - the endpoint refuses it either way.
    if (id === null || this.saving || this.sessionActive) {
      return false;
    }

    this.persist(id);
    return true;
  }

  private persist(id: number): void {
    if (this.sessionActive) {
      return;
    }

    // A created product already carries `GstRules.None`, so "no rule" is stored by the create itself
    // and needs no second write - and must not be sent as anything else.
    if (this.rule === GstClassification.Unknown) {
      this.persisted.emit();
      return;
    }

    this.saving = true;
    this.error = '';
    this.productService.setGstRule(id, this.rule).subscribe({
      next: () => {
        this.saving = false;
        this.persisted.emit();
      },
      error: () => {
        this.saving = false;
        this.error =
          'The product was created, but its GST rule was not saved. Press Retry GST rule to try again; ' +
          'it saves the rule only and never creates a second product. Other changes on the form ' +
          'are not saved again - edit the product from the Products list instead.';
        this.failed.emit();
      }
    });
  }
}
