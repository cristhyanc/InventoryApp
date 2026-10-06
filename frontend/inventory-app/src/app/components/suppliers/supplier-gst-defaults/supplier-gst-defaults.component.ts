import { Component, Input, OnChanges, OnDestroy, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Observable, of, Subject, Subscription } from 'rxjs';
import { catchError, map, switchMap, tap } from 'rxjs/operators';
import { SupplierService } from '../../../services/supplier.service';
import { GstClassification, SupplierGstDefaults } from '../../../models/models';

/** A read tagged with the supplier it was issued for; `defaults` is null when the read failed. */
type SupplierGstDefaultsRead = { id: number; defaults: SupplierGstDefaults | null };

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
export class SupplierGstDefaultsComponent implements OnChanges, OnDestroy {
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

  /**
   * False until the stored defaults of the supplier currently on screen have been read back
   * successfully. The form starts on "no default" placeholders, so saving before that - in
   * particular after a failed load - would erase the defaults the supplier already has. Saving
   * stays disabled while this is false.
   */
  loaded = false;

  loading = false;
  saving = false;
  saved = false;
  error = '';

  private readonly requestedSupplierId = new Subject<number | null>();
  private readonly loads: Subscription;

  constructor(private supplierService: SupplierService) {
    // switchMap cancels the in-flight read whenever the selected supplier changes, so opening
    // supplier A and then B can never let A's slower response populate B's form - and a later save
    // write A's values onto B. The identity check discards a response that arrives regardless.
    this.loads = this.requestedSupplierId
      .pipe(
        tap(() => this.reset()),
        switchMap((id): Observable<SupplierGstDefaultsRead | null> => {
          if (id === null) {
            return of(null);
          }

          this.loading = true;
          return this.supplierService.getGstDefaults(id).pipe(
            map((defaults) => ({ id, defaults })),
            catchError(() => of({ id, defaults: null }))
          );
        })
      )
      .subscribe((result) => {
        if (result === null || result.id !== this.supplierId) {
          return;
        }

        this.loading = false;
        if (result.defaults === null) {
          this.error =
            'Failed to load the GST defaults. Saving stays disabled until they load; reload the page to try again.';
          return;
        }

        this.form = {
          productLineGstDefault: result.defaults.productLineGstDefault,
          deliveryGstDefault: result.defaults.deliveryGstDefault,
          packageGstDefault: result.defaults.packageGstDefault
        };
        this.loaded = true;
      });
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['supplierId']) {
      this.requestedSupplierId.next(this.supplierId);
    }
  }

  ngOnDestroy(): void {
    this.loads.unsubscribe();
    this.requestedSupplierId.complete();
  }

  save(): void {
    const id = this.supplierId;
    if (id === null || !this.loaded || this.saving) {
      return;
    }

    this.saving = true;
    this.saved = false;
    this.error = '';
    this.supplierService.setGstDefaults(id, { ...this.form }).subscribe({
      next: () => {
        // A save that finishes after another supplier was opened reports nothing: its outcome
        // belongs to the supplier it was issued for, not to the form now on screen.
        if (id !== this.supplierId) {
          return;
        }

        this.saving = false;
        this.saved = true;
      },
      error: () => {
        if (id !== this.supplierId) {
          return;
        }

        this.saving = false;
        this.error = 'Failed to save the GST defaults.';
      }
    });
  }

  private reset(): void {
    this.form = {
      productLineGstDefault: GstClassification.Unknown,
      deliveryGstDefault: GstClassification.Unknown,
      packageGstDefault: GstClassification.Unknown
    };
    this.loaded = false;
    this.loading = false;
    this.saving = false;
    this.saved = false;
    this.error = '';
  }
}
