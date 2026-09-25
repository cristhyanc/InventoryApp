import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ImportService } from '../../services/import.service';
import { NayaxCostBackfillResult, ReportingService, SiteCommissionAgreement } from '../../services/reporting.service';
import { ToastService } from '../../services/toast.service';
import { NayaxProcessingFeeRate, NayaxSettingsService } from '../../services/nayax-settings.service';
import { Site } from '../../models/models';
import { SiteService } from '../../services/site.service';
import { Product } from '../../models/models';
import { ProductService } from '../../services/product.service';
import {
  InventoryCostBaselineSource,
  InventoryCostTransitionBatchPreview,
  InventoryCostTransitionPreview,
  InventoryCostTransitionService
} from '../../services/inventory-cost-transition.service';

@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="mb-6">
      <h1 class="text-2xl font-semibold text-slate-800">Admin</h1>
      <p class="mt-1 text-sm text-slate-500">Imports and maintenance tools. These actions can change application data.</p>
    </div>

    @if (error) { <div class="mb-5 rounded-lg bg-red-50 p-4 text-sm text-red-700">{{ error }}</div> }
    <div class="grid gap-5 md:grid-cols-2">
      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Nayax Settings</h2>
        <p class="mt-1 text-sm text-slate-500">Used for current-period reporting when actual Nayax processing fee data has not yet been imported. Imported reimbursement data always takes precedence.</p>
        <div class="mt-4 grid gap-3 sm:grid-cols-3">
          <label class="text-sm text-slate-700 sm:col-span-2">Estimated Processing Fee per Card Transaction (ex GST)
            <input type="number" min="0" step="0.0001" required class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="feeExGst" />
          </label>
          <label class="text-sm text-slate-700">Effective from
            <input type="date" required class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="effectiveFrom" />
          </label>
        </div>
        <button type="button" class="mt-3 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="saveFeeRate()">Save rate</button>
        <div class="mt-5 border-t border-slate-100 pt-4">
          <h3 class="mb-2 text-sm font-semibold text-slate-700">Configured settings</h3>
          @if (feeRates.length) {
            <div class="overflow-x-auto">
              <table class="min-w-full text-left text-sm">
                <thead class="bg-slate-50 text-xs text-slate-600">
                  <tr><th class="px-3 py-2">Effective from</th><th class="px-3 py-2">Fee per card transaction (ex GST)</th><th class="px-3 py-2">Status</th></tr>
                </thead>
                <tbody>
                  @for (rate of feeRates; track rate.id ?? rate.effectiveFrom) {
                    <tr class="border-t border-slate-100">
                      <td class="px-3 py-2">{{ rate.effectiveFrom | date:'dd/MM/yyyy' }}</td>
                      <td class="px-3 py-2">{{ rate.feeExGst | currency:'AUD':'symbol':'1.4-4' }}</td>
                      <td class="px-3 py-2">
                        @if (isCurrentFeeRate(rate)) {
                          <span class="rounded-full bg-emerald-100 px-2 py-1 text-xs font-medium text-emerald-700">Current</span>
                        } @else if (isFutureFeeRate(rate)) {
                          <span class="rounded-full bg-blue-100 px-2 py-1 text-xs font-medium text-blue-700">Scheduled</span>
                        } @else {
                          <span class="text-xs text-slate-500">Previous</span>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          } @else {
            <p class="text-sm text-slate-500">No Nayax processing-fee settings configured.</p>
          }
        </div>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Site Commission Agreement</h2>
        <p class="mt-1 text-sm text-slate-500">Rates are effective-dated and apply to sales from the selected date onward.</p>
        <div class="mt-4 grid gap-3 sm:grid-cols-2">
          <label class="text-sm text-slate-700">Site<select class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="commissionSiteId"><option [ngValue]="null">Select a site</option>@for (site of sites; track site.siteId) { <option [ngValue]="site.siteId">{{ site.siteName }}</option> }</select></label>
          <label class="text-sm text-slate-700">Rate (%)<input type="number" min="0" max="100" step="0.01" class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="commissionRate" /></label>
          <label class="text-sm text-slate-700">Effective from<input type="date" class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="commissionEffectiveFrom" /></label>
          <label class="text-sm text-slate-700">Frequency<select class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="commissionFrequency"><option [ngValue]="0">None</option><option [ngValue]="1">Monthly</option><option [ngValue]="2">Quarterly</option></select></label>
          <label class="text-sm text-slate-700">Basis<select class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="commissionBasis"><option [ngValue]="0">Gross Sales</option><option [ngValue]="1">Card Sales</option><option [ngValue]="2">Sales ex GST</option></select></label>
          <label class="text-sm text-slate-700">Due days after period end (optional)<input type="number" min="0" class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2" [(ngModel)]="commissionDueDays" /></label>
        </div>
        <button type="button" class="mt-3 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="saveCommissionAgreement()">Save agreement</button>
        @if (commissionAgreements.length) {
          <div class="mt-5 overflow-x-auto border-t border-slate-100 pt-4">
            <h3 class="mb-2 text-sm font-semibold text-slate-700">Current agreements</h3>
            <table class="min-w-full text-left text-xs">
              <thead class="bg-slate-50 text-slate-600"><tr><th class="px-2 py-2">Site</th><th class="px-2 py-2">Effective</th><th class="px-2 py-2">Rate</th><th class="px-2 py-2">Frequency</th><th class="px-2 py-2">Basis</th><th class="px-2 py-2">Due days</th></tr></thead>
              <tbody>@for (agreement of commissionAgreements; track agreement.id) { <tr class="border-t"><td class="px-2 py-2">{{ siteName(agreement.siteId) }}</td><td class="px-2 py-2">{{ agreement.effectiveFrom | date:'dd/MM/yyyy' }}@if (agreement.effectiveTo) { - {{ agreement.effectiveTo | date:'dd/MM/yyyy' }}}</td><td class="px-2 py-2">{{ agreement.commissionRate * 100 | number:'1.2-2' }}%</td><td class="px-2 py-2">{{ frequencyLabel(agreement.frequency) }}</td><td class="px-2 py-2">{{ basisLabel(agreement.basis) }}</td><td class="px-2 py-2">{{ agreement.paymentDueDaysAfterPeriodEnd ?? '—' }}</td></tr> }</tbody>
            </table>
          </div>
        } @else { <p class="mt-4 text-sm text-slate-500">No site commission agreements have been configured.</p> }
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Import Nayax sales</h2>
        <p class="mt-1 text-sm text-slate-500">Import .xlsx, .xls, or .csv sales data.</p>
        <input #salesFile type="file" accept=".xlsx,.xls,.csv" class="hidden" (change)="onSalesSelected($event)" />
        <div class="mt-4 flex flex-wrap gap-2">
          <button type="button" class="rounded-md border border-slate-300 px-3 py-2 text-sm" (click)="downloadTemplate()">Download template</button>
          <button type="button" class="rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="salesFile.click()">{{ loading ? 'Importing...' : 'Choose sales file' }}</button>
        </div>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Import products</h2>
        <p class="mt-1 text-sm text-slate-500">Refresh the product catalogue from the configured source.</p>
        <button type="button" class="mt-4 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="importProducts()">Import products</button>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Import XML files</h2>
        <p class="mt-1 text-sm text-slate-500">Process pending reimbursement XML files.</p>
        <button type="button" class="mt-4 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50" [disabled]="loading" (click)="importXml()">Import XML files</button>
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
              <div>Cutoff timestamp: <strong>{{ transitionBatchPreview.cutoffAt | date:'medium' }}</strong></div>
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
              <div>Cutoff timestamp: <strong>{{ transitionPreview.cutoffAt | date:'medium' }}</strong></div>
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
    </div>
  `
})
export class AdminComponent {
  loading = false;
  error = '';
  nayaxBackfillResult: NayaxCostBackfillResult | null = null;
  feeRates: NayaxProcessingFeeRate[] = [];
  feeExGst = 0.17;
  effectiveFrom = new Date().toISOString().slice(0, 10);
  sites: Site[] = [];
  commissionSiteId: number | null = null;
  commissionRate = 0;
  commissionEffectiveFrom = new Date().toISOString().slice(0, 10);
  commissionFrequency = 0;
  commissionBasis = 0;
  commissionDueDays: number | null = null;
  commissionAgreements: SiteCommissionAgreement[] = [];
  products: Product[] = [];
  transitionProductId: number | null = null;
  transitionAverageUnitCost = 0;
  transitionCostSource = InventoryCostBaselineSource.ManualAuthoritative;
  transitionPreview: InventoryCostTransitionPreview | null = null;
  transitionBatchPreview: InventoryCostTransitionBatchPreview | null = null;
  transitionAllProducts = false;
  readonly baselineSources = InventoryCostBaselineSource;

  constructor(
    private importService: ImportService,
    private reportingService: ReportingService,
    private toast: ToastService,
    private nayaxSettings: NayaxSettingsService,
    private siteService: SiteService,
    private productService: ProductService,
    private inventoryCostTransition: InventoryCostTransitionService
  ) {
    this.loadFeeRates();
    this.loadCommissionAgreements();
    this.siteService.getAll().subscribe({ next: sites => this.sites = sites, error: () => this.sites = [] });
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

  saveCommissionAgreement(): void {
    if (this.commissionSiteId == null || this.commissionRate < 0 || this.commissionRate > 100 || !this.commissionEffectiveFrom || (this.commissionDueDays != null && this.commissionDueDays < 0)) {
      this.toast.error('Enter a site, valid commission rate, and effective date.');
      return;
    }
    this.loading = true;
    this.reportingService.saveSiteCommissionAgreement({ siteId: this.commissionSiteId, commissionRate: this.commissionRate / 100, effectiveFrom: this.commissionEffectiveFrom, frequency: this.commissionFrequency, basis: this.commissionBasis, paymentDueDaysAfterPeriodEnd: this.commissionDueDays }).subscribe({
      next: () => { this.loading = false; this.toast.success('Site commission agreement saved.'); this.loadCommissionAgreements(); },
      error: err => { this.loading = false; this.toast.error(this.extractErrorMessage(err, 'Unable to save the commission agreement.')); }
    });
  }

  loadCommissionAgreements(): void {
    this.reportingService.siteCommissionAgreements().subscribe({
      next: agreements => this.commissionAgreements = agreements,
      error: () => this.toast.error('Unable to load site commission agreements.')
    });
  }

  siteName(siteId: number): string { return this.sites.find(site => site.siteId === siteId)?.siteName ?? `Site ${siteId}`; }
  frequencyLabel(frequency: number): string { return ['None', 'Monthly', 'Quarterly'][frequency] ?? 'Unknown'; }
  basisLabel(basis: number): string { return ['Gross Sales', 'Card Sales', 'Sales ex GST'][basis] ?? 'Unknown'; }
  isCurrentFeeRate(rate: NayaxProcessingFeeRate): boolean {
    return this.currentFeeRate?.effectiveFrom === rate.effectiveFrom;
  }
  isFutureFeeRate(rate: NayaxProcessingFeeRate): boolean {
    return new Date(`${rate.effectiveFrom.slice(0, 10)}T00:00:00`).getTime() > new Date().setHours(0, 0, 0, 0);
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

  private get currentFeeRate(): NayaxProcessingFeeRate | undefined {
    const today = new Date().setHours(23, 59, 59, 999);
    return this.feeRates.find(rate =>
      new Date(`${rate.effectiveFrom.slice(0, 10)}T00:00:00`).getTime() <= today);
  }

  loadFeeRates(): void {
    this.nayaxSettings.getRates().subscribe({
      next: rates => {
        this.feeRates = rates;
        if (rates[0]) this.feeExGst = rates[0].feeExGst;
      },
      error: () => this.toast.error('Failed to load Nayax settings.')
    });
  }

  saveFeeRate(): void {
    if (this.feeExGst === null || this.feeExGst < 0 || !this.effectiveFrom) {
      this.toast.error('Enter a non-negative fee and effective date.');
      return;
    }
    this.loading = true;
    this.nayaxSettings.saveRate({ effectiveFrom: this.effectiveFrom, feeExGst: this.feeExGst }).subscribe({
      next: () => { this.loading = false; this.toast.success('Nayax processing-fee rate saved.'); this.loadFeeRates(); },
      error: err => { this.loading = false; this.toast.error(err?.error ?? 'Failed to save Nayax settings.'); }
    });
  }

  onSalesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    if (!/\.(xlsx|xls|csv)$/i.test(file.name)) {
      this.toast.error('Only .xlsx, .xls, or .csv files are supported.');
      return;
    }
    this.loading = true;
    this.importService.importNayaxSales(file).subscribe({
      next: result => { this.loading = false; this.toast.success(`Imported ${result.imported} sales, updated ${result.updated}, skipped ${result.skipped}.`); },
      error: err => { this.loading = false; this.toast.error(err?.error?.message ?? err?.error ?? 'Failed to import Nayax sales.'); }
    });
  }

  downloadTemplate(): void {
    const headers = ['TransactionID', 'TransactionStatusId', 'MachineID', 'NayaxProductId', 'MachineName', 'SettlementValue', 'PaymentMethod', 'ProductName', 'Product Cost Price', 'MachineAuthorizationTime'];
    const sample = ['1001', '12', '42', '987654', 'Machine A', '12.50', 'Card', 'Coke Zero', '1.100000', '2026-09-02 14:30:00'];
    const url = URL.createObjectURL(new Blob([[headers.join(','), sample.join(',')].join('\n')], { type: 'text/csv;charset=utf-8;' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'nayax-sales-import-template.csv'; anchor.click(); URL.revokeObjectURL(url);
  }

  importProducts(): void {
    this.loading = true;
    this.importService.importProducts().subscribe({
      next: () => { this.loading = false; this.toast.success('Products imported successfully.'); },
      error: () => { this.loading = false; this.toast.error('Failed to import products.'); }
    });
  }

  importXml(): void {
    this.loading = true;
    this.importService.importPendingXmlFiles().subscribe({
      next: result => { this.loading = false; this.toast.success(`Imported ${result.importedFiles} file(s) and ${result.importedReimbursements} reimbursement(s).`); },
      error: err => { this.loading = false; this.toast.error(err?.error ?? 'The XML import could not be started.'); }
    });
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
