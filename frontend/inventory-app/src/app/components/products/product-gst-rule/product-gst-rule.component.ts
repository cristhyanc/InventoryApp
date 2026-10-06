import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ProductService } from '../../../services/product.service';
import { GstClassification } from '../../../models/models';

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
export class ProductGstRuleComponent implements OnChanges {
  @Input() productId: number | null = null;

  /** `Unknown` is "no rule", a state of its own; it is never the same as an explicit GST-free rule. */
  readonly options: { value: GstClassification; label: string }[] = [
    { value: GstClassification.Unknown, label: 'No rule' },
    { value: GstClassification.Taxable, label: 'Taxable' },
    { value: GstClassification.GstFree, label: 'GST-free' }
  ];

  rule: GstClassification = GstClassification.Unknown;
  loading = false;
  saving = false;
  saved = false;
  error = '';

  constructor(private productService: ProductService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['productId']) {
      this.load();
    }
  }

  save(): void {
    const id = this.productId;
    if (id === null) {
      return;
    }

    this.saving = true;
    this.saved = false;
    this.error = '';
    this.productService.setGstRule(id, this.rule).subscribe({
      next: () => {
        this.saving = false;
        this.saved = true;
      },
      error: () => {
        this.saving = false;
        this.error = 'Failed to save the GST rule.';
      }
    });
  }

  private load(): void {
    this.rule = GstClassification.Unknown;
    this.saved = false;
    this.error = '';
    const id = this.productId;
    if (id === null) {
      return;
    }

    this.loading = true;
    this.productService.getGstRule(id).subscribe({
      next: (configured) => {
        this.loading = false;
        this.rule = configured.gstRule;
      },
      error: () => {
        this.loading = false;
        this.error = 'Failed to load the GST rule.';
      }
    });
  }
}
