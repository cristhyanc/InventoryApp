import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges } from '@angular/core';
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
 * exists through `[createdProductId]`; everything after that is this component's, and it reports the
 * one thing the page needs back: whether the rule is stored.
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
export class ProductCreateGstRuleComponent implements OnChanges, OnDestroy {
  /**
   * The id `POST api/products` answered, or `null` while no product exists yet.
   *
   * Receiving an id is the signal to store the rule: it is the first moment there is a product to
   * store it on. The page keeps the id, because the page is what created the product.
   */
  @Input() createdProductId: number | null = null;

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

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['createdProductId'] && this.createdProductId !== null) {
      this.persist(this.createdProductId);
    }
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  /**
   * Retries the rule write after a failure, with the rule on screen now, so a corrected choice is
   * the one stored. It writes the rule and nothing else: the product already exists, and creating a
   * second one is exactly what this retry must never do.
   */
  save(): void {
    const id = this.createdProductId;
    // Checked here as well as disabled in the template, so the write is actually unavailable rather
    // than merely inert - the endpoint refuses it either way.
    if (id === null || this.saving || this.sessionActive) {
      return;
    }

    this.persist(id);
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
          'The product was created, but its GST rule was not saved. Press Save GST rule to retry; ' +
          'it saves the rule only and never creates a second product. Other changes on the form ' +
          'above are not saved again - edit the product from the Products list instead.';
        this.failed.emit();
      }
    });
  }
}
