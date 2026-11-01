import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  SuperAdminMasterProduct,
  SuperAdminMasterProductsService
} from '../../../services/super-admin-master-products.service';
import {
  MASTER_PRODUCT_SIZE_UNIT_LABELS,
  MasterProductSizeUnit
} from '../../../services/super-admin-price-catalogue-review.service';
import { extractReviewErrorMessage } from '../price-catalogue-review/review-error';

/**
 * The one form that adds a master product to the shared catalogue and corrects one (issue #1024,
 * parent #817): the brand, the name or variant, and the individual size with its unit from the closed
 * list.
 *
 * One component for both, because they are one form over one set of values and the only difference is
 * which request it sends - a `null` {@link product} is a new master product and a given one is a
 * correction of that row.
 *
 * It is a dedicated feature component rather than markup in the Master products page, per
 * docs/architecture.md § Page composition boundary: it owns its own form values, its submission state
 * and its error, and the routed page composes it through `[product]` and reacts to `(saved)` and
 * `(cancelled)`.
 *
 * **It decides nothing.** The normalisation, the key two descriptions are compared by, the duplicate
 * rule, the refusal of a size change under a confirmed supplier listing and the refusal of a stale
 * edit are all the server's, and a refused submission shows **the server's own sentence** rather than
 * a second copy of the rule. The only thing withheld locally is a submission with an incomplete form -
 * no brand, no name or no size - because a request that cannot describe a product is not one.
 *
 * **A correction presents the instant it read.** `lastChangedAtUtc` is the `updatedAtUtc` the page
 * published for that row, sent back unchanged: the server refuses an edit presenting an instant the
 * row no longer carries, so two Super Admin tabs cannot overwrite each other silently.
 *
 * **Nothing about any business is here**, in either direction: the contract carries no business id,
 * business name or tracking count, so there is nothing of the kind to show or to send.
 */
@Component({
  selector: 'app-super-admin-master-product-form',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './master-product-form.component.html'
})
export class MasterProductFormComponent implements OnChanges {
  /** The master product being corrected, or `null` to add one. */
  @Input() product: SuperAdminMasterProduct | null = null;

  /** Raised after a write the server accepted, so the page can re-read the catalogue. */
  @Output() readonly saved = new EventEmitter<string>();

  /** Raised when the form is dismissed without writing anything. */
  @Output() readonly cancelled = new EventEmitter<void>();

  brand = '';
  name = '';
  sizeValue: number | null = null;
  sizeUnit: MasterProductSizeUnit = MasterProductSizeUnit.Millilitre;

  submitting = false;
  error: string | null = null;

  readonly sizeUnits = Object.entries(MASTER_PRODUCT_SIZE_UNIT_LABELS).map(([value, label]) => ({
    value: Number(value) as MasterProductSizeUnit,
    label
  }));

  constructor(private readonly masterProducts: SuperAdminMasterProductsService) {}

  /**
   * A different product - or switching between adding and correcting - starts from that product's own
   * values. Without this, a half-typed correction could be carried onto another row.
   */
  ngOnChanges(): void {
    this.error = null;
    this.submitting = false;
    this.brand = this.product?.brand ?? '';
    this.name = this.product?.name ?? '';
    this.sizeValue = this.product?.sizeValue ?? null;
    this.sizeUnit = this.product?.sizeUnit ?? MasterProductSizeUnit.Millilitre;
  }

  get editing(): boolean {
    return this.product !== null;
  }

  get heading(): string {
    return this.editing ? 'Edit master product' : 'New master product';
  }

  /**
   * Whether this product has a confirmed supplier listing, which is the one case the server will
   * refuse a size change in.
   *
   * It is used to *say so* beside the size fields, never to decide anything: the refusal is the
   * server's, and a page that hid the fields would also hide the reason a correction was impossible.
   */
  get hasConfirmedListing(): boolean {
    return (this.product?.listingCounts.confirmed ?? 0) > 0;
  }

  submit(): void {
    if (this.brand.trim() === '' || this.name.trim() === '' || this.sizeValue === null) {
      this.error = 'A master product needs a brand, a name and a size.';
      return;
    }

    const description = {
      brand: this.brand.trim(),
      name: this.name.trim(),
      sizeValue: this.sizeValue,
      sizeUnit: this.sizeUnit
    };

    this.submitting = true;
    this.error = null;

    const product = this.product;
    const write = product
      ? this.masterProducts.update(product.id, {
          ...description,
          // The instant this page read for that row, sent back unchanged.
          lastChangedAtUtc: product.updatedAtUtc
        })
      : this.masterProducts.create(description);

    write.subscribe({
      next: (result) => {
        this.submitting = false;
        this.saved.emit(
          product
            ? `${result.brand} ${result.name} was corrected.`
            : `${result.brand} ${result.name} was added to the catalogue.`
        );
      },
      error: (err: unknown) => {
        this.submitting = false;
        this.error = extractReviewErrorMessage(
          err,
          product ? 'Unable to correct this master product.' : 'Unable to add this master product.'
        );
      }
    });
  }

  cancel(): void {
    this.cancelled.emit();
  }
}
