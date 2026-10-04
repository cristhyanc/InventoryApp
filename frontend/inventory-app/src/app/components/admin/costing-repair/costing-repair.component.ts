import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Product } from '../../../models/models';
import { ToastService } from '../../../services/toast.service';
import {
  InventoryCostRepairApplied,
  InventoryCostRepairPreview,
  InventoryCostRepairRecord,
  InventoryCostRepairService
} from '../../../services/inventory-cost-repair.service';
import { BusinessDateTimePipe } from '../../../formatting/business-date-time.pipe';
import {
  BUSINESS_TIME_ZONE,
  currentDateTimeInTimeZone,
  fromDateTimeLocalValue,
  toDateTimeLocalValue,
  zonedDateTimeToUtc
} from '../../../formatting/business-time-zone';

/**
 * The Admin page's Costing Repair workflow (issue #361, the UI over #359/#360's repair API): the
 * operator selects a product with a fatal MissingOpening/UnknownCost costing issue, previews a
 * costing-only historical repair, applies it once satisfied, and reviews the product's repair
 * history. It owns the whole workflow's form, preview, apply and history state and its own API
 * calls and notifications, so `AdminComponent` only has to supply the product list - the same
 * page-composition boundary `MachineRestockSyncComponent` follows for `MachineDetailComponent`
 * (see docs/architecture.md § Page composition boundary).
 *
 * The effective date/time is entered and displayed in Sydney time (`BUSINESS_TIME_ZONE`) and
 * converted to/from the UTC instant the API contract requires, through the same IANA-timezone-
 * aware conversion `business-time-zone.ts` already uses for other operator-facing date/time
 * input.
 */
@Component({
  selector: 'app-costing-repair',
  standalone: true,
  imports: [CommonModule, FormsModule, BusinessDateTimePipe],
  template: `
    <section class="rounded-xl bg-white p-6 shadow-sm md:col-span-2">
      <h2 class="text-lg font-semibold text-slate-800">Costing Repair</h2>
      <p class="mt-1 text-sm text-slate-500">
        A human-entered historical costing repair for a product whose cost history has a fatal
        missing-opening or unknown-cost issue. It changes this product's historical cost of goods
        sold from the effective time onward; it never changes physical stock, and applying it is
        not proof that the recorded history is correct - only an auditable correction for a
        specific, explained situation. Use a real purchase or stock correction instead when
        physical stock, not costing history, is what is wrong.
      </p>

      <div class="mt-4 grid gap-3 sm:grid-cols-3">
        <label class="text-sm text-slate-700">Product
          <select
            class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2"
            [(ngModel)]="productId"
            (ngModelChange)="selectProduct()"
          >
            <option [ngValue]="null">Select a product</option>
            @for (product of products; track product.id) {
              <option [ngValue]="product.id">{{ product.name }}</option>
            }
          </select>
        </label>
        <label class="text-sm text-slate-700">Quantity
          <input
            type="number" min="1" step="1"
            class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2"
            [(ngModel)]="quantity"
          />
        </label>
        <label class="text-sm text-slate-700">Unit cost
          <input
            type="number" min="0" step="0.000001"
            class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2"
            [(ngModel)]="unitCost"
          />
        </label>
        <label class="text-sm text-slate-700 sm:col-span-2">Reason
          <input
            type="text"
            class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2"
            placeholder="A specific, auditable reason for this repair"
            [(ngModel)]="reason"
          />
        </label>
        <label class="text-sm text-slate-700">Effective date/time (Sydney time)
          <input
            type="datetime-local"
            class="mt-1 block w-full rounded-md border border-slate-300 px-3 py-2"
            [(ngModel)]="effectiveAtLocal"
          />
        </label>
      </div>

      <button
        type="button"
        class="mt-3 rounded-md border border-blue-300 px-3 py-2 text-sm text-blue-700 disabled:opacity-50"
        [disabled]="loading || productId == null"
        (click)="previewRepair()"
      >Preview repair</button>

      @if (preview) {
        <div class="mt-5 rounded-lg border border-slate-200 p-4">
          <div class="grid gap-2 text-sm text-slate-700 sm:grid-cols-3">
            <div>Product: <strong>{{ preview.productName }}</strong></div>
            <div>Effective: <strong>{{ preview.effectiveAt | businessDateTime }}</strong></div>
            <div>Quantity added: <strong>{{ preview.quantity }}</strong></div>
            <div>Unit cost: <strong>{{ preview.unitCost | currency:'AUD':'symbol':'1.2-6' }}</strong></div>
            <div>Value added: <strong>{{ preview.totalValue | currency:'AUD' }}</strong></div>
            <div>Costing quantity before: <strong>{{ preview.costingQuantityBefore }}</strong></div>
            <div>Inventory value before: <strong>{{ preview.inventoryValueBefore | currency:'AUD' }}</strong></div>
            <div>Costing quantity after: <strong>{{ preview.costingQuantityAfter }}</strong></div>
            <div>Inventory value after: <strong>{{ preview.inventoryValueAfter | currency:'AUD' }}</strong></div>
            <div>Average unit cost after: <strong>{{ preview.averageUnitCostAfter != null ? (preview.averageUnitCostAfter | currency:'AUD':'symbol':'1.2-6') : '—' }}</strong></div>
            <div>Projected costing quantity: <strong>{{ preview.projectedCostingQuantity }}</strong></div>
            <div>Projected inventory value: <strong>{{ preview.projectedInventoryValue | currency:'AUD' }}</strong></div>
            <div>Projected average unit cost: <strong>{{ preview.projectedAverageUnitCost != null ? (preview.projectedAverageUnitCost | currency:'AUD':'symbol':'1.2-6') : '—' }}</strong></div>
          </div>

          @if (preview.firstUncostableSale) {
            <p class="mt-3 text-sm text-slate-600">
              First previously uncostable completed sale: transaction {{ preview.firstUncostableSale.transactionId }}
              at {{ preview.firstUncostableSale.authorizationTime | businessDateTime }}.
              @if (!preview.replaysBeforeFirstUncostableSale) {
                <span class="font-medium text-amber-700"> This repair's effective time does not replay before that sale, so it will not cost it.</span>
              }
            </p>
          } @else {
            <p class="mt-3 text-sm text-slate-600">No completed sale is currently uncostable for this product.</p>
          }

          @if (preview.remainingFatalIssues.length) {
            <div class="mt-3 rounded-md bg-amber-50 p-3 text-sm text-amber-700">
              <p class="font-medium">This repair would still leave these issues behind, and cannot be applied until they are resolved:</p>
              <ul class="mt-1 list-disc pl-5">
                @for (issue of preview.remainingFatalIssues; track issue.code) {
                  <li>{{ issue.message }}</li>
                }
              </ul>
            </div>
          } @else {
            <p class="mt-3 text-sm text-emerald-700">No fatal costing issues would remain after this repair.</p>
          }

          <button
            type="button"
            class="mt-4 rounded-md bg-amber-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50"
            [disabled]="loading"
            (click)="applyRepair()"
          >Apply repair</button>
        </div>
      }

      @if (productId != null) {
        <div class="mt-5 border-t border-slate-100 pt-4">
          <h3 class="mb-2 text-sm font-semibold text-slate-700">Repair history</h3>
          @if (history.length) {
            <div class="overflow-x-auto">
              <table class="min-w-full text-left text-sm">
                <thead class="bg-slate-50 text-xs text-slate-600">
                  <tr>
                    <th class="px-3 py-2">Effective</th>
                    <th class="px-3 py-2">Quantity</th>
                    <th class="px-3 py-2">Unit cost</th>
                    <th class="px-3 py-2">Value</th>
                    <th class="px-3 py-2">Reason</th>
                    <th class="px-3 py-2">Recorded</th>
                  </tr>
                </thead>
                <tbody>
                  @for (repair of history; track repair.id) {
                    <tr class="border-t border-slate-100">
                      <td class="px-3 py-2">{{ repair.effectiveAt | businessDateTime }}</td>
                      <td class="px-3 py-2">{{ repair.quantity }}</td>
                      <td class="px-3 py-2">{{ repair.unitCost | currency:'AUD':'symbol':'1.2-6' }}</td>
                      <td class="px-3 py-2">{{ repair.totalValue | currency:'AUD' }}</td>
                      <td class="px-3 py-2">{{ repair.reason }}</td>
                      <td class="px-3 py-2">{{ repair.createdAt | businessDateTime }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          } @else {
            <p class="text-sm text-slate-500">No costing repairs recorded for this product.</p>
          }
        </div>
      }
    </section>
  `
})
export class CostingRepairComponent {
  @Input() products: Product[] = [];

  loading = false;
  productId: number | null = null;
  quantity: number | null = null;
  unitCost: number | null = null;
  reason = '';
  effectiveAtLocal = toDateTimeLocalValue(currentDateTimeInTimeZone(new Date(), BUSINESS_TIME_ZONE));
  preview: InventoryCostRepairPreview | null = null;
  history: InventoryCostRepairRecord[] = [];

  constructor(
    private readonly repairService: InventoryCostRepairService,
    private readonly toast: ToastService
  ) {}

  selectProduct(): void {
    this.preview = null;
    this.loadHistory();
  }

  previewRepair(): void {
    if (this.productId == null) {
      this.toast.error('Select a product to preview a costing repair.');
      return;
    }
    if (!this.quantity || this.quantity <= 0) {
      this.toast.error('Enter a positive quantity.');
      return;
    }
    if (this.unitCost == null || this.unitCost < 0) {
      this.toast.error('Enter a non-negative unit cost.');
      return;
    }
    if (!this.reason.trim()) {
      this.toast.error('Record a specific reason for this costing repair.');
      return;
    }
    const effectiveAt = this.effectiveAtUtcIso();
    if (!effectiveAt) {
      this.toast.error('Enter a valid effective date/time.');
      return;
    }

    this.loading = true;
    this.preview = null;
    this.repairService.preview({
      productId: this.productId,
      effectiveAt,
      quantity: this.quantity,
      unitCost: this.unitCost,
      reason: this.reason
    }).subscribe({
      next: preview => { this.preview = preview; this.loading = false; },
      error: err => { this.loading = false; this.toast.error(this.extractErrorMessage(err, 'Unable to preview the costing repair.')); }
    });
  }

  applyRepair(): void {
    if (!this.preview ||
        !window.confirm(
          "Save this human-entered historical costing repair? It changes this product's historical cost of goods sold and does not change physical stock. It is not proof the recorded cost history is correct."
        )) return;

    this.loading = true;
    this.repairService.apply(this.preview).subscribe({
      next: (applied: InventoryCostRepairApplied) => {
        this.loading = false;
        this.preview = null;
        this.quantity = null;
        this.unitCost = null;
        this.reason = '';
        this.toast.success(`Costing repair saved. Recosted ${applied.recostedSaleCount} sale(s).`);
        this.loadHistory();
      },
      error: err => {
        this.loading = false;
        // The preview may now be stale (its own error message asks to preview again); clearing it
        // forces a fresh preview before another apply can be attempted.
        this.preview = null;
        this.toast.error(this.extractErrorMessage(err, 'Unable to apply the costing repair.'));
      }
    });
  }

  private loadHistory(): void {
    if (this.productId == null) {
      this.history = [];
      return;
    }
    this.repairService.history(this.productId).subscribe({
      next: history => this.history = history,
      error: () => this.toast.error('Unable to load the costing repair history.')
    });
  }

  private effectiveAtUtcIso(): string | null {
    const wallClock = fromDateTimeLocalValue(this.effectiveAtLocal);
    if (!wallClock) return null;
    return zonedDateTimeToUtc(
      wallClock.year, wallClock.month, wallClock.day, wallClock.hour, wallClock.minute, BUSINESS_TIME_ZONE
    ).toISOString();
  }

  // Some admin actions return a plain string error body rather than a ProblemDetails object;
  // both shapes are handled here, matching the same fallback already used in admin.component.ts.
  private extractErrorMessage(err: unknown, fallback: string): string {
    const body = (err as { error?: unknown } | undefined)?.error;
    if (typeof body === 'string') return body;
    const problem = body as { message?: string; title?: string } | undefined;
    return problem?.message ?? problem?.title ?? fallback;
  }
}
