import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { SupplierService } from '../../../services/supplier.service';
import { GstClassification } from '../../../models/models';

/**
 * Supplier GST default configuration (issue #430). It owns its own fetch, loading, save and error
 * state, so the suppliers page only has to supply the selected supplier's identity.
 *
 * The three defaults are separate values: delivery and package charges never inherit the
 * product-line default, because a supplier can sell GST-free goods and still charge GST on
 * delivery. A default is explicit configuration - a supplier being GST-registered is never enough
 * to classify its products - and it only applies where the purchased product has no rule of its
 * own. Saving it reclassifies no purchase that is already recorded.
 */
@Component({
  selector: 'app-supplier-gst-defaults',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './supplier-gst-defaults.component.html'
})
export class SupplierGstDefaultsComponent implements OnChanges {
  @Input() supplierId: number | null = null;

  /** Named in the heading so it is unambiguous which supplier these defaults belong to. */
  @Input() supplierName: string | null = null;

  /** `Unknown` is "no default", a state of its own; it is never the same as an explicit GST-free default. */
  readonly options: { value: GstClassification; label: string }[] = [
    { value: GstClassification.Unknown, label: 'No default' },
    { value: GstClassification.Taxable, label: 'Taxable' },
    { value: GstClassification.GstFree, label: 'GST-free' }
  ];

  form = {
    productLineGstDefault: GstClassification.Unknown,
    deliveryGstDefault: GstClassification.Unknown,
    packageGstDefault: GstClassification.Unknown
  };

  loading = false;
  saving = false;
  saved = false;
  error = '';

  constructor(private supplierService: SupplierService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['supplierId']) {
      this.load();
    }
  }

  save(): void {
    const id = this.supplierId;
    if (id === null) {
      return;
    }

    this.saving = true;
    this.saved = false;
    this.error = '';
    this.supplierService.setGstDefaults(id, { ...this.form }).subscribe({
      next: () => {
        this.saving = false;
        this.saved = true;
      },
      error: () => {
        this.saving = false;
        this.error = 'Failed to save the GST defaults.';
      }
    });
  }

  private load(): void {
    this.form = {
      productLineGstDefault: GstClassification.Unknown,
      deliveryGstDefault: GstClassification.Unknown,
      packageGstDefault: GstClassification.Unknown
    };
    this.saved = false;
    this.error = '';
    const id = this.supplierId;
    if (id === null) {
      return;
    }

    this.loading = true;
    this.supplierService.getGstDefaults(id).subscribe({
      next: (defaults) => {
        this.loading = false;
        this.form = {
          productLineGstDefault: defaults.productLineGstDefault,
          deliveryGstDefault: defaults.deliveryGstDefault,
          packageGstDefault: defaults.packageGstDefault
        };
      },
      error: () => {
        this.loading = false;
        this.error = 'Failed to load the GST defaults.';
      }
    });
  }
}
