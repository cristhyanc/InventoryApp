import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Product } from '../../../models/models';
import { ToastService } from '../../../services/toast.service';
import {
  InventoryCostBaselineSource,
  InventoryCostTransitionBatchPreview,
  InventoryCostTransitionPreview,
  InventoryCostTransitionService
} from '../../../services/inventory-cost-transition.service';
import { BusinessDateTimePipe } from '../../../formatting/business-date-time.pipe';

/**
 * The Inventory AVCO Transition Baseline workflow as a dedicated feature component, composed by
 * the routed `AvcoTransitionComponent` page through `[products]`/`(baselinesSaved)` per
 * docs/architecture.md § Page composition boundary (issue #191): the preview/apply form, its own
 * state, actions, loading/error lifecycle, confirmations and notifications live here, not in the
 * page. The workflow itself is unchanged by that move and by the earlier split out of
 * `AdminComponent` (issue #390, Admin split 3/3): the operator previews either one product's
 * opening position or every product without a baseline, chooses the cost reliability
 * (authoritative/estimated), and confirms before anything is saved. `InventoryCostTransitionService`
 * and the four use cases behind it remain the sole authority for the opening quantity/value, the
 * cutoff instant, the legacy-replay discrepancy and the cost rebuild, so no costing calculation or
 * persistence rule is reimplemented here; Apply always resubmits exactly the previewed object
 * rather than the form's current values, as before. The cutoff is displayed in Sydney time through
 * `BusinessDateTimePipe` (issue #232).
 */
@Component({
  selector: 'app-avco-transition-workflow',
  standalone: true,
  imports: [CommonModule, FormsModule, BusinessDateTimePipe],
  template: `
    <section class="card">
      <div class="card-header">
        <h2 class="card-title">Inventory AVCO Transition Baseline</h2>
      </div>
      <div class="card-body">
        <p class="text-sm value-muted">Start reliable perpetual AVCO from a controlled cutover without changing incomplete legacy movements or historical Nayax-costed sales.</p>
        <label class="mt-4 flex items-center gap-2 text-sm font-medium text-md-gray-800">
          <input type="checkbox" [(ngModel)]="transitionAllProducts" (ngModelChange)="changeTransitionScope()" />
          Create baselines for all products that do not already have one
        </label>
        <div class="mt-4 grid gap-4 sm:grid-cols-3">
          <div class="field">
            <label class="field-label" for="avco-transition-product">Product</label>
            <select id="avco-transition-product" [disabled]="transitionAllProducts" [(ngModel)]="transitionProductId" (ngModelChange)="selectTransitionProduct()">
              <option [ngValue]="null">Select a product</option>
              @for (product of products; track product.id) {
                <option [ngValue]="product.id">{{ product.name }}</option>
              }
            </select>
          </div>
          <div class="field">
            <label class="field-label" for="avco-transition-cost">Verified opening average unit cost</label>
            <input id="avco-transition-cost" type="number" min="0" step="0.000001" [disabled]="transitionAllProducts" [(ngModel)]="transitionAverageUnitCost" />
            @if (transitionAllProducts) { <p class="field-hint">Each product's current AverageUnitCost will be shown for confirmation.</p> }
          </div>
          <div class="field">
            <label class="field-label" for="avco-transition-source">Cost reliability</label>
            <select id="avco-transition-source" [(ngModel)]="transitionCostSource">
              <option [ngValue]="baselineSources.ManualAuthoritative">Authoritative</option>
              <option [ngValue]="baselineSources.ManualEstimated">Estimated</option>
            </select>
          </div>
        </div>
        <button type="button" class="btn btn-primary mt-3" [disabled]="loading || (!transitionAllProducts && transitionProductId == null)" (click)="previewTransition()">{{ transitionAllProducts ? 'Preview all product baselines' : 'Preview transition baseline' }}</button>

        @if (transitionBatchPreview) {
          <div class="mt-5 rounded-lg border border-md-gray-200 p-4">
            <div class="grid gap-2 text-sm text-md-gray-800 sm:grid-cols-4">
              <div>Products: <strong>{{ transitionBatchPreview.productCount }}</strong></div>
              <div>Total home stock: <strong>{{ transitionBatchPreview.homeStockQuantity }}</strong></div>
              <div>Total machine stock: <strong>{{ transitionBatchPreview.machineStockQuantity }}</strong></div>
              <div>Total CostingQuantity: <strong>{{ transitionBatchPreview.openingCostingQuantity }}</strong></div>
              <div>Total InventoryValue: <strong>{{ transitionBatchPreview.inventoryValue | currency:'AUD' }}</strong></div>
              <div>Cutoff timestamp: <strong>{{ transitionBatchPreview.cutoffAt | businessDateTime }}</strong></div>
              <div>Cost source: <strong>{{ transitionSourceLabel(transitionBatchPreview.costSource) }}</strong></div>
            </div>
            <div class="mt-4 max-h-96 overflow-auto">
              <table class="table">
                <thead class="table-head sticky top-0"><tr><th scope="col" class="table-cell">Product</th><th scope="col" class="table-cell">Home</th><th scope="col" class="table-cell">Machines</th><th scope="col" class="table-cell">Total</th><th scope="col" class="table-cell">Average cost</th><th scope="col" class="table-cell">Value</th><th scope="col" class="table-cell">Legacy discrepancy</th></tr></thead>
                <tbody>
                  @for (product of transitionBatchPreview.products; track product.productId) {
                    <tr class="table-row">
                      <td class="table-cell">
                        <details><summary class="cursor-pointer font-medium">{{ product.productName }}</summary>
                          <div class="mt-2 text-xs value-muted">
                            @for (machine of product.machineStocks; track machine.machineId) {
                              <div>{{ machine.machineName }}: {{ machine.stockQuantity }}</div>
                            }
                          </div>
                        </details>
                      </td>
                      <td class="table-cell">{{ product.homeStockQuantity }}</td>
                      <td class="table-cell">{{ product.machineStockQuantity }}</td>
                      <td class="table-cell font-medium">{{ product.openingCostingQuantity }}</td>
                      <td class="table-cell">{{ product.averageUnitCost | currency:'AUD':'symbol':'1.2-6' }}</td>
                      <td class="table-cell">{{ product.inventoryValue | currency:'AUD' }}</td>
                      <td class="table-cell">{{ product.legacyPhysicalDiscrepancy }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <button type="button" class="btn btn-primary mt-4" [disabled]="loading" (click)="applyAllTransitions()">Confirm and save all product baselines</button>
          </div>
        }

        @if (transitionPreview && !transitionBatchPreview) {
          <div class="mt-5 rounded-lg border border-md-gray-200 p-4">
            <div class="grid gap-2 text-sm text-md-gray-800 sm:grid-cols-3">
              <div>Home stock quantity: <strong>{{ transitionPreview.homeStockQuantity }}</strong></div>
              <div>Machine stock quantity: <strong>{{ transitionPreview.machineStockQuantity }}</strong></div>
              <div>Total proposed CostingQuantity: <strong>{{ transitionPreview.openingCostingQuantity }}</strong></div>
              <div>Proposed AverageUnitCost: <strong>{{ transitionPreview.averageUnitCost | currency:'AUD':'symbol':'1.2-6' }}</strong></div>
              <div>Proposed InventoryValue: <strong>{{ transitionPreview.inventoryValue | currency:'AUD' }}</strong></div>
              <div>Cutoff timestamp: <strong>{{ transitionPreview.cutoffAt | businessDateTime }}</strong></div>
              <div>Cost source: <strong>{{ transitionSourceLabel(transitionPreview.costSource) }}</strong></div>
              <div>Legacy replayed physical quantity: <strong>{{ transitionPreview.legacyReplayedPhysicalQuantity }}</strong></div>
              <div>Legacy discrepancy retired: <strong>{{ transitionPreview.legacyPhysicalDiscrepancy }}</strong></div>
            </div>
            @if (transitionPreview.dataQualityNote) {
              <div class="alert alert-warning mt-3">{{ transitionPreview.dataQualityNote }}</div>
            }
            <div class="mt-4 overflow-x-auto">
              <table class="table">
                <thead class="table-head"><tr><th scope="col" class="table-cell">Machine</th><th scope="col" class="table-cell">Stock</th><th scope="col" class="table-cell">Source</th></tr></thead>
                <tbody>
                  @for (machine of transitionPreview.machineStocks; track machine.machineId) {
                    <tr class="table-row"><td class="table-cell">{{ machine.machineName }}</td><td class="table-cell">{{ machine.stockQuantity }}</td><td class="table-cell">{{ machine.source }}</td></tr>
                  }
                </tbody>
              </table>
            </div>
            <button type="button" class="btn btn-primary mt-4" [disabled]="loading" (click)="applyTransition()">Confirm and save transition baseline</button>
          </div>
        }
      </div>
    </section>
  `
})
export class AvcoTransitionWorkflowComponent {
  /** The product list the single-product baseline selects from, loaded by the host page. */
  @Input() products: Product[] = [];

  /**
   * Raised after a baseline (or a batch of them) is saved, so the host page can reload the
   * products whose `averageUnitCost` the save may have changed. The workflow does not reach back
   * into the page's state or its `ProductService` to do that itself.
   */
  @Output() readonly baselinesSaved = new EventEmitter<void>();

  loading = false;
  transitionProductId: number | null = null;
  transitionAverageUnitCost = 0;
  transitionCostSource = InventoryCostBaselineSource.ManualAuthoritative;
  transitionPreview: InventoryCostTransitionPreview | null = null;
  transitionBatchPreview: InventoryCostTransitionBatchPreview | null = null;
  transitionAllProducts = false;
  readonly baselineSources = InventoryCostBaselineSource;

  constructor(
    private readonly toast: ToastService,
    private readonly inventoryCostTransition: InventoryCostTransitionService
  ) {}

  selectTransitionProduct(): void {
    const product = this.products.find(x => x.id === this.transitionProductId);
    this.transitionAverageUnitCost = product?.averageUnitCost ?? 0;
    this.transitionPreview = null;
    this.transitionBatchPreview = null;
  }

  changeTransitionScope(): void {
    this.transitionPreview = null;
    this.transitionBatchPreview = null;
  }

  transitionSourceLabel(source: InventoryCostBaselineSource): string {
    return source === InventoryCostBaselineSource.ManualAuthoritative ? 'Authoritative' : 'Estimated';
  }

  previewTransition(): void {
    if (this.transitionAllProducts) {
      this.loading = true;
      this.transitionPreview = null;
      this.transitionBatchPreview = null;
      this.inventoryCostTransition.previewAll(this.transitionCostSource).subscribe({
        next: preview => { this.transitionBatchPreview = preview; this.loading = false; },
        error: err => { this.loading = false; this.toast.error(this.extractErrorMessage(err, 'Unable to preview all transition baselines.')); }
      });
      return;
    }
    if (this.transitionProductId == null || this.transitionAverageUnitCost < 0) {
      this.toast.error('Select a product and enter a non-negative verified average unit cost.');
      return;
    }
    this.loading = true;
    this.transitionPreview = null;
    this.transitionBatchPreview = null;
    this.inventoryCostTransition.preview(
      this.transitionProductId,
      this.transitionAverageUnitCost,
      this.transitionCostSource
    ).subscribe({
      next: preview => { this.transitionPreview = preview; this.loading = false; },
      error: err => { this.loading = false; this.toast.error(this.extractErrorMessage(err, 'Unable to preview the transition baseline.')); }
    });
  }

  applyTransition(): void {
    if (!this.transitionPreview ||
        !window.confirm('Save this exact opening quantity, cost, machine-stock snapshot, and cutoff as the permanent AVCO transition baseline?')) return;
    this.loading = true;
    this.inventoryCostTransition.apply(this.transitionPreview).subscribe({
      next: () => {
        this.loading = false;
        this.transitionPreview = null;
        this.toast.success('Inventory AVCO transition baseline saved.');
        this.baselinesSaved.emit();
      },
      error: err => { this.loading = false; this.toast.error(this.extractErrorMessage(err, 'Unable to save the transition baseline.')); }
    });
  }

  applyAllTransitions(): void {
    if (!this.transitionBatchPreview ||
        !window.confirm(`Save the displayed AVCO transition baselines for all ${this.transitionBatchPreview.productCount} products?`)) return;
    this.loading = true;
    this.inventoryCostTransition.applyAll(this.transitionBatchPreview).subscribe({
      next: result => {
        this.loading = false;
        this.transitionBatchPreview = null;
        this.toast.success(`${result.productCount} inventory AVCO transition baselines saved.`);
        this.baselinesSaved.emit();
      },
      error: err => { this.loading = false; this.toast.error(this.extractErrorMessage(err, 'Unable to save all transition baselines.')); }
    });
  }

  // Some of these actions moved from returning a plain string body to a ProblemDetails object
  // (issue #59); older endpoints still return a plain string, so both shapes are handled here,
  // matching the same fallback already used in machine-detail.component.ts.
  private extractErrorMessage(err: unknown, fallback: string): string {
    const body = (err as { error?: unknown } | undefined)?.error;
    if (typeof body === 'string') return body;
    const problem = body as { message?: string; title?: string } | undefined;
    return problem?.message ?? problem?.title ?? fallback;
  }
}
