import { Component, Input, OnChanges, OnDestroy, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Observable, of, Subject, Subscription } from 'rxjs';
import { catchError, map, switchMap, tap } from 'rxjs/operators';
import { ProductService } from '../../../services/product.service';
import { GstClassification, ProductGstRule } from '../../../models/models';

/** A read tagged with the product it was issued for; `configured` is null when the read failed. */
type ProductGstRuleRead = { id: number; configured: ProductGstRule | null };

/**
 * Product GST rule configuration (issue #430). It owns its own fetch, loading, save and error
 * state, so the product-edit page only has to supply the product identity.
 *
 * It saves separately from the product form on purpose. The rule is bookkeeping configuration on
 * its own API resource, not a catalogue field: saving it changes no name, price, cost or stock, and
 * it never reclassifies a purchase that is already recorded. Historical classification is the
 * Admin Preview/Apply maintenance workflow (issue #433), not this page.
 */
@Component({
  selector: 'app-product-gst-rule',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './product-gst-rule.component.html'
})
export class ProductGstRuleComponent implements OnChanges, OnDestroy {
  @Input() productId: number | null = null;

  /** `Unknown` is "no rule", a state of its own; it is never the same as an explicit GST-free rule. */
  readonly options: { value: GstClassification; label: string }[] = [
    { value: GstClassification.Unknown, label: 'No rule' },
    { value: GstClassification.Taxable, label: 'Taxable' },
    { value: GstClassification.GstFree, label: 'GST-free' }
  ];

  rule: GstClassification = GstClassification.Unknown;

  /**
   * False until the stored rule of the product currently on screen has been read back successfully.
   * The picker starts on the "no rule" placeholder, so saving before that - in particular after a
   * failed load - would erase the rule the product already has. Saving stays disabled while this is
   * false.
   */
  loaded = false;

  loading = false;
  saving = false;
  saved = false;
  error = '';

  private readonly requestedProductId = new Subject<number | null>();
  private readonly loads: Subscription;

  constructor(private productService: ProductService) {
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
            'Failed to load the GST rule. Saving stays disabled until it loads; reload the page to try again.';
          return;
        }

        this.rule = result.configured.gstRule;
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
    this.requestedProductId.complete();
  }

  save(): void {
    const id = this.productId;
    if (id === null || !this.loaded || this.saving) {
      return;
    }

    this.saving = true;
    this.saved = false;
    this.error = '';
    this.productService.setGstRule(id, this.rule).subscribe({
      next: () => {
        // A save that finishes after another product was opened reports nothing: its outcome
        // belongs to the product it was issued for, not to the picker now on screen.
        if (id !== this.productId) {
          return;
        }

        this.saving = false;
        this.saved = true;
      },
      error: () => {
        if (id !== this.productId) {
          return;
        }

        this.saving = false;
        this.error = 'Failed to save the GST rule.';
      }
    });
  }

  private reset(): void {
    this.rule = GstClassification.Unknown;
    this.loaded = false;
    this.loading = false;
    this.saving = false;
    this.saved = false;
    this.error = '';
  }
}
