import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { NayaxCostBackfillResult, ReportingService } from '../../services/reporting.service';
import { ToastService } from '../../services/toast.service';
import { Product } from '../../models/models';
import { ProductService } from '../../services/product.service';
import {
  InventoryCostBaselineSource,
  InventoryCostTransitionBatchPreview,
  InventoryCostTransitionPreview,
  InventoryCostTransitionService
} from '../../services/inventory-cost-transition.service';
import { BusinessDateTimePipe } from '../../formatting/business-date-time.pipe';
import { CostingRepairComponent } from './costing-repair/costing-repair.component';

@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, BusinessDateTimePipe, CostingRepairComponent],
  template: `
    <div class="mb-6">
      <h1 class="text-2xl font-semibold text-slate-800">Admin</h1>
      <p class="mt-1 text-sm text-slate-500">Imports and maintenance tools. These actions can change application data.</p>
    </div>

    @if (error) { <div class="mb-5 rounded-lg bg-red-50 p-4 text-sm text-red-700">{{ error }}</div> }
    <div class="grid gap-5 md:grid-cols-2">
      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Nayax Settings</h2>
        <p class="mt-1 text-sm text-slate-500">Processing-fee rate configuration and configured-rate history.</p>
        <a routerLink="/admin/nayax-settings" class="mt-4 inline-block rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white">Open Nayax Settings</a>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Site Commission Agreements</h2>
        <p class="mt-1 text-sm text-slate-500">Site commission agreement form and current agreements.</p>
        <a routerLink="/admin/site-commission-agreements" class="mt-4 inline-block rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white">Open Site Commission Agreements</a>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Imports</h2>
        <p class="mt-1 text-sm text-slate-500">Nayax sales file import and template, product catalogue import and pending reimbursement XML import.</p>
        <a routerLink="/admin/imports" class="mt-4 inline-block rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white">Open Imports</a>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Nayax Historical Cost Recovery</h2>
        <p class="mt-1 text-sm text-slate-500">Recover pending completed-sale COGS from transaction-level Nayax Product Cost Price without replacing finalized inventory-ledger costs.</p>
        <div class="mt-4 flex flex-wrap gap-2">
          <button type="button" class="rounded-md border border-blue-300 px-3 py-2 text-sm text-blue-700" [disabled]="loading" (click)="backfillNayax(true)">Dry Run</button>
          <button type="button" class="rounded-md bg-amber-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="applyNayaxBackfill()">Apply Nayax Cost Backfill</button>
        </div>
        @if (nayaxBackfillResult) {
          <div class="mt-4 grid grid-cols-2 gap-2 text-sm text-slate-600">
            <div>Pending completed sales: <strong>{{ nayaxBackfillResult.salesWouldBeCosted + nayaxBackfillResult.salesStillPending }}</strong></div>
            <div>With Nayax historical cost: <strong>{{ nayaxBackfillResult.salesWithNayaxCost }}</strong></div>
            <div>Recoverable: <strong>{{ nayaxBackfillResult.salesWouldBeCosted }}</strong></div>
            <div>Still missing cost: <strong>{{ nayaxBackfillResult.salesStillPending }}</strong></div>
            <div>Already costed: <strong>{{ nayaxBackfillResult.salesAlreadyCosted }}</strong></div>
            <div>Invalid cost rows: <strong>{{ nayaxBackfillResult.invalidCostRows }}</strong></div>
          </div>
        }
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm md:col-span-2">
        <h2 class="text-lg font-semibold text-slate-800">Inventory AVCO Transition Baseline</h2>
        <p class="mt-1 text-sm text-slate-500">Start reliable perpetual AVCO from a controlled cutover without changing incomplete legacy movements or historical Nayax-costed sales.</p>
        <label class="mt-4 flex items-center gap-2 text-sm font-medium text-slate-700">
          <input type="checkbox" [(ngModel)]="transitionAllProducts" (ngModelChange)="changeTransitionScope()" />
          Create baselines for all products that do not already have one
        </label>
        <div class="mt-4 grid gap-3 sm:grid-cols-3">
          <label class="text-sm text-slate-700">Product
            <select class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2 disabled:bg-slate-100" [disabled]="transitionAllProducts" [(ngModel)]="transitionProductId" (ngModelChange)="selectTransitionProduct()">
              <option [ngValue]="null">Select a product</option>
              @for (product of products; track product.id) {
                <option [ngValue]="product.id">{{ product.name }}</option>
              }
            </select>
          </label>
          <label class="text-sm text-slate-700">Verified opening average unit cost
            <input type="number" min="0" step="0.000001" class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2 disabled:bg-slate-100" [disabled]="transitionAllProducts" [(ngModel)]="transitionAverageUnitCost" />
            @if (transitionAllProducts) { <span class="mt-1 block text-xs text-slate-500">Each product's current AverageUnitCost will be shown for confirmation.</span> }
          </label>
          <label class="text-sm text-slate-700">Cost reliability
            <select class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="transitionCostSource">
              <option [ngValue]="baselineSources.ManualAuthoritative">Authoritative</option>
              <option [ngValue]="baselineSources.ManualEstimated">Estimated</option>
            </select>
          </label>
        </div>
        <button type="button" class="mt-3 rounded-md border border-blue-300 px-3 py-2 text-sm text-blue-700 disabled:opacity-50" [disabled]="loading || (!transitionAllProducts && transitionProductId == null)" (click)="previewTransition()">{{ transitionAllProducts ? 'Preview all product baselines' : 'Preview transition baseline' }}</button>

        @if (transitionBatchPreview) {
          <div class="mt-5 rounded-lg border border-slate-200 p-4">
            <div class="grid gap-2 text-sm text-slate-700 sm:grid-cols-4">
              <div>Products: <strong>{{ transitionBatchPreview.productCount }}</strong></div>
              <div>Total home stock: <strong>{{ transitionBatchPreview.homeStockQuantity }}</strong></div>
              <div>Total machine stock: <strong>{{ transitionBatchPreview.machineStockQuantity }}</strong></div>
              <div>Total CostingQuantity: <strong>{{ transitionBatchPreview.openingCostingQuantity }}</strong></div>
              <div>Total InventoryValue: <strong>{{ transitionBatchPreview.inventoryValue | currency:'AUD' }}</strong></div>
              <div>Cutoff timestamp: <strong>{{ transitionBatchPreview.cutoffAt | businessDateTime }}</strong></div>
              <div>Cost source: <strong>{{ transitionSourceLabel(transitionBatchPreview.costSource) }}</strong></div>
            </div>
            <div class="mt-4 max-h-96 overflow-auto">
              <table class="min-w-full text-left text-sm">
                <thead class="sticky top-0 bg-slate-50 text-xs text-slate-600"><tr><th class="px-3 py-2">Product</th><th class="px-3 py-2">Home</th><th class="px-3 py-2">Machines</th><th class="px-3 py-2">Total</th><th class="px-3 py-2">Average cost</th><th class="px-3 py-2">Value</th><th class="px-3 py-2">Legacy discrepancy</th></tr></thead>
                <tbody>
                  @for (product of transitionBatchPreview.products; track product.productId) {
                    <tr class="border-t border-slate-100">
                      <td class="px-3 py-2">
                        <details><summary class="cursor-pointer font-medium">{{ product.productName }}</summary>
                          <div class="mt-2 text-xs text-slate-500">
                            @for (machine of product.machineStocks; track machine.machineId) {
                              <div>{{ machine.machineName }}: {{ machine.stockQuantity }}</div>
                            }
                          </div>
                        </details>
                      </td>
                      <td class="px-3 py-2">{{ product.homeStockQuantity }}</td>
                      <td class="px-3 py-2">{{ product.machineStockQuantity }}</td>
                      <td class="px-3 py-2 font-medium">{{ product.openingCostingQuantity }}</td>
                      <td class="px-3 py-2">{{ product.averageUnitCost | currency:'AUD':'symbol':'1.2-6' }}</td>
                      <td class="px-3 py-2">{{ product.inventoryValue | currency:'AUD' }}</td>
                      <td class="px-3 py-2">{{ product.legacyPhysicalDiscrepancy }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <button type="button" class="mt-4 rounded-md bg-amber-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="applyAllTransitions()">Confirm and save all product baselines</button>
          </div>
        }

        @if (transitionPreview && !transitionBatchPreview) {
          <div class="mt-5 rounded-lg border border-slate-200 p-4">
            <div class="grid gap-2 text-sm text-slate-700 sm:grid-cols-3">
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
            <p class="mt-3 text-sm text-amber-700">{{ transitionPreview.dataQualityNote }}</p>
            <div class="mt-4 overflow-x-auto">
              <table class="min-w-full text-left text-sm">
                <thead class="bg-slate-50 text-xs text-slate-600"><tr><th class="px-3 py-2">Machine</th><th class="px-3 py-2">Stock</th><th class="px-3 py-2">Source</th></tr></thead>
                <tbody>
                  @for (machine of transitionPreview.machineStocks; track machine.machineId) {
                    <tr class="border-t border-slate-100"><td class="px-3 py-2">{{ machine.machineName }}</td><td class="px-3 py-2">{{ machine.stockQuantity }}</td><td class="px-3 py-2">{{ machine.source }}</td></tr>
                  }
                </tbody>
              </table>
            </div>
            <button type="button" class="mt-4 rounded-md bg-amber-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="applyTransition()">Confirm and save transition baseline</button>
          </div>
        }
      </section>

      <app-costing-repair [products]="products"></app-costing-repair>
    </div>
  `
})
export class AdminComponent {
  loading = false;
  error = '';
  nayaxBackfillResult: NayaxCostBackfillResult | null = null;
  products: Product[] = [];
  transitionProductId: number | null = null;
  transitionAverageUnitCost = 0;
  transitionCostSource = InventoryCostBaselineSource.ManualAuthoritative;
  transitionPreview: InventoryCostTransitionPreview | null = null;
  transitionBatchPreview: InventoryCostTransitionBatchPreview | null = null;
  transitionAllProducts = false;
  readonly baselineSources = InventoryCostBaselineSource;

  constructor(
    private reportingService: ReportingService,
    private toast: ToastService,
    private productService: ProductService,
    private inventoryCostTransition: InventoryCostTransitionService
  ) {
    this.productService.getAll().subscribe({ next: products => this.products = products, error: () => this.products = [] });
  }

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
        this.productService.getAll().subscribe(products => this.products = products);
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
        this.productService.getAll().subscribe(products => this.products = products);
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

  applyNayaxBackfill(): void {
    if (window.confirm('Apply transaction-level Nayax historical costs to eligible pending completed sales?')) {
      this.backfillNayax(false);
    }
  }

  backfillNayax(dryRun: boolean): void {
    this.loading = true;
    this.error = '';
    this.reportingService.backfillNayaxSaleCosts(dryRun).subscribe({
      next: result => { this.nayaxBackfillResult = result; this.loading = false; },
      error: err => { this.error = typeof err?.error === 'string' ? err.error : 'Unable to run the Nayax cost backfill.'; this.loading = false; }
    });
  }
}
