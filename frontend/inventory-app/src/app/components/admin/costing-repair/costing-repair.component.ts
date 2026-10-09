import { Component, Input, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
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
  resolveZonedDateTime,
  toDateTimeLocalValue
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
 * input. A time that does not exist in Sydney (the hour skipped when daylight saving starts) or
 * that occurs twice (the hour repeated when it ends) is rejected with guidance rather than
 * silently moved or guessed, because the effective time decides which historical sales the
 * repair affects.
 *
 * Switching product discards every in-flight preview and history response for the previous
 * product (each request carries a sequence number and its subscription is cancelled), and Apply
 * refuses a preview that does not belong to the currently selected product.
 */
@Component({
  selector: 'app-costing-repair',
  standalone: true,
  imports: [CommonModule, FormsModule, BusinessDateTimePipe],
  template: `
    <section class="card md:col-span-2">
      <div class="card-header">
        <h2 class="card-title">Costing Repair</h2>
      </div>
      <div class="card-body">
        <p class="text-sm value-muted">
          A human-entered historical costing repair for a product whose cost history has a fatal
          missing-opening or unknown-cost issue. It changes this product's historical cost of goods
          sold from the effective time onward; it never changes physical stock, and applying it is
          not proof that the recorded history is correct - only an auditable correction for a
          specific, explained situation. Use a real purchase or stock correction instead when
          physical stock, not costing history, is what is wrong.
        </p>

        <div class="mt-4 grid gap-4 sm:grid-cols-3">
          <div class="field">
            <label class="field-label" for="costing-repair-product">Product</label>
            <select
              id="costing-repair-product"
              [(ngModel)]="productId"
              (ngModelChange)="selectProduct()"
              [disabled]="applying"
            >
              <option [ngValue]="null">Select a product</option>
              @for (product of products; track product.id) {
                <option [ngValue]="product.id">{{ product.name }}</option>
              }
            </select>
          </div>
          <div class="field">
            <label class="field-label" for="costing-repair-quantity">Quantity</label>
            <input id="costing-repair-quantity" type="number" min="1" step="1" [(ngModel)]="quantity" />
          </div>
          <div class="field">
            <label class="field-label" for="costing-repair-unit-cost">Unit cost</label>
            <input id="costing-repair-unit-cost" type="number" min="0" step="0.000001" [(ngModel)]="unitCost" />
          </div>
          <div class="field sm:col-span-2">
            <label class="field-label" for="costing-repair-reason">Reason</label>
            <input
              id="costing-repair-reason"
              type="text"
              placeholder="A specific, auditable reason for this repair"
              [(ngModel)]="reason"
            />
          </div>
          <div class="field">
            <label class="field-label" for="costing-repair-effective-at">Effective date/time (Sydney time)</label>
            <input
              id="costing-repair-effective-at"
              type="datetime-local"
              [(ngModel)]="effectiveAtLocal"
              (ngModelChange)="effectiveAtError = null"
            />
            @if (effectiveAtError) {
              <p class="field-error">{{ effectiveAtError }}</p>
            }
          </div>
        </div>

        <button
          type="button"
          class="btn btn-primary mt-3"
          [disabled]="loading || productId == null"
          (click)="previewRepair()"
        >Preview repair</button>

        @if (preview) {
          <div class="mt-5 rounded-lg border border-md-gray-200 p-4">
            <div class="grid gap-2 text-sm text-md-gray-800 sm:grid-cols-3">
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
              <p class="mt-3 text-sm value-muted">
                First previously uncostable completed sale: transaction {{ preview.firstUncostableSale.transactionId }}
                at {{ preview.firstUncostableSale.authorizationTime | businessDateTime }}.
                @if (!preview.replaysBeforeFirstUncostableSale) {
                  <span class="font-medium text-md-warning-text"> This repair's effective time does not replay before that sale, so it will not cost it.</span>
                }
              </p>
            } @else {
              <p class="mt-3 text-sm value-muted">No completed sale is currently uncostable for this product.</p>
            }

            @if (preview.remainingFatalIssues.length) {
              <div class="alert alert-warning mt-3">
                <p class="alert-title">This repair would still leave these issues behind, and cannot be applied until they are resolved:</p>
                <ul class="mt-1 list-disc pl-5">
                  @for (issue of preview.remainingFatalIssues; track issue.code) {
                    <li>{{ issue.message }}</li>
                  }
                </ul>
              </div>
            } @else {
              <p class="mt-3 text-sm text-md-success-text">No fatal costing issues would remain after this repair.</p>
            }

            <button
              type="button"
              class="btn btn-primary mt-4"
              [disabled]="loading"
              (click)="applyRepair()"
            >Apply repair</button>
          </div>
        }

        @if (productId != null) {
          <div class="mt-5 border-t border-md-gray-200 pt-4">
            <h3 class="mb-2 text-sm font-semibold text-md-gray-800">Repair history</h3>
            @if (historyLoading) {
              <p class="text-sm value-muted">Loading repair history…</p>
            } @else if (historyError) {
              <p class="text-sm text-md-danger-text">The costing repair history could not be loaded.</p>
            } @else if (history.length) {
              <div class="overflow-x-auto">
                <table class="table">
                  <thead class="table-head">
                    <tr>
                      <th scope="col" class="table-cell">Effective</th>
                      <th scope="col" class="table-cell">Quantity</th>
                      <th scope="col" class="table-cell">Unit cost</th>
                      <th scope="col" class="table-cell">Value</th>
                      <th scope="col" class="table-cell">Reason</th>
                      <th scope="col" class="table-cell">Recorded</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (repair of history; track repair.id) {
                      <tr class="table-row">
                        <td class="table-cell">{{ repair.effectiveAt | businessDateTime }}</td>
                        <td class="table-cell">{{ repair.quantity }}</td>
                        <td class="table-cell">{{ repair.unitCost | currency:'AUD':'symbol':'1.2-6' }}</td>
                        <td class="table-cell">{{ repair.totalValue | currency:'AUD' }}</td>
                        <td class="table-cell">{{ repair.reason }}</td>
                        <td class="table-cell">{{ repair.createdAt | businessDateTime }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            } @else {
              <p class="text-sm value-muted">No costing repairs recorded for this product.</p>
            }
          </div>
        }
      </div>
    </section>
  `
})
export class CostingRepairComponent implements OnDestroy {
  @Input() products: Product[] = [];

  previewLoading = false;
  applying = false;
  historyLoading = false;
  historyError = false;
  productId: number | null = null;
  quantity: number | null = null;
  unitCost: number | null = null;
  reason = '';
  effectiveAtLocal = toDateTimeLocalValue(currentDateTimeInTimeZone(new Date(), BUSINESS_TIME_ZONE));
  effectiveAtError: string | null = null;
  preview: InventoryCostRepairPreview | null = null;
  history: InventoryCostRepairRecord[] = [];

  // The product the current preview was requested for, and a sequence number per request kind:
  // a response is only accepted while its sequence number is still the latest, so a response for
  // a product the operator has since switched away from (including A -> B -> A) is discarded.
  private previewProductId: number | null = null;
  private previewSequence = 0;
  private historySequence = 0;
  private previewSubscription: Subscription | null = null;
  private historySubscription: Subscription | null = null;

  constructor(
    private readonly repairService: InventoryCostRepairService,
    private readonly toast: ToastService
  ) {}

  get loading(): boolean {
    return this.previewLoading || this.applying;
  }

  ngOnDestroy(): void {
    this.cancelPreview();
    this.cancelHistory();
  }

  selectProduct(): void {
    this.cancelPreview();
    this.preview = null;
    this.previewProductId = null;
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
      this.toast.error(this.effectiveAtError ?? 'Enter a valid effective date/time.');
      return;
    }

    this.cancelPreview();
    const sequence = ++this.previewSequence;
    const productId = this.productId;
    this.previewLoading = true;
    this.preview = null;
    this.previewProductId = null;
    this.previewSubscription = this.repairService.preview({
      productId,
      effectiveAt,
      quantity: this.quantity,
      unitCost: this.unitCost,
      reason: this.reason
    }).subscribe({
      next: preview => {
        if (sequence !== this.previewSequence) return;
        this.preview = preview;
        this.previewProductId = productId;
        this.previewLoading = false;
      },
      error: err => {
        if (sequence !== this.previewSequence) return;
        this.previewLoading = false;
        this.toast.error(this.extractErrorMessage(err, 'Unable to preview the costing repair.'));
      }
    });
  }

  applyRepair(): void {
    if (!this.preview) return;
    // The apply boundary: only a preview requested for, and returned for, the product currently
    // selected may be applied. Anything else is discarded and must be previewed again.
    if (this.productId == null ||
        this.previewProductId !== this.productId ||
        this.preview.productId !== this.productId) {
      this.preview = null;
      this.previewProductId = null;
      this.toast.error('This preview is not for the selected product. Preview the repair again before applying it.');
      return;
    }
    if (!window.confirm(
          "Save this human-entered historical costing repair? It changes this product's historical cost of goods sold and does not change physical stock. It is not proof the recorded cost history is correct."
        )) return;

    // The product selector is disabled while applying, so the selection cannot change under it.
    this.applying = true;
    this.repairService.apply(this.preview).subscribe({
      next: (applied: InventoryCostRepairApplied) => {
        this.applying = false;
        this.preview = null;
        this.previewProductId = null;
        this.quantity = null;
        this.unitCost = null;
        this.reason = '';
        this.toast.success(`Costing repair saved. Recosted ${applied.recostedSaleCount} sale(s).`);
        this.loadHistory();
      },
      error: err => {
        this.applying = false;
        // The preview may now be stale (its own error message asks to preview again); clearing it
        // forces a fresh preview before another apply can be attempted.
        this.preview = null;
        this.previewProductId = null;
        this.toast.error(this.extractErrorMessage(err, 'Unable to apply the costing repair.'));
      }
    });
  }

  private loadHistory(): void {
    this.cancelHistory();
    const sequence = ++this.historySequence;
    // Never show another product's records while this product's history loads or fails.
    this.history = [];
    this.historyError = false;
    if (this.productId == null) {
      this.historyLoading = false;
      return;
    }
    this.historyLoading = true;
    this.historySubscription = this.repairService.history(this.productId).subscribe({
      next: history => {
        if (sequence !== this.historySequence) return;
        this.history = history;
        this.historyLoading = false;
      },
      error: () => {
        if (sequence !== this.historySequence) return;
        this.historyLoading = false;
        this.historyError = true;
        this.toast.error('Unable to load the costing repair history.');
      }
    });
  }

  private cancelPreview(): void {
    this.previewSequence++;
    this.previewSubscription?.unsubscribe();
    this.previewSubscription = null;
    this.previewLoading = false;
  }

  private cancelHistory(): void {
    this.historySequence++;
    this.historySubscription?.unsubscribe();
    this.historySubscription = null;
  }

  private effectiveAtUtcIso(): string | null {
    this.effectiveAtError = null;
    const wallClock = fromDateTimeLocalValue(this.effectiveAtLocal);
    if (!wallClock) return null;
    const resolution = resolveZonedDateTime(
      wallClock.year, wallClock.month, wallClock.day, wallClock.hour, wallClock.minute, BUSINESS_TIME_ZONE
    );
    switch (resolution.kind) {
      case 'valid':
        return resolution.utc.toISOString();
      case 'nonexistent':
        this.effectiveAtError =
          'This time does not exist in Sydney: the clocks skip forward an hour when daylight saving starts. ' +
          'Enter a time outside the skipped hour.';
        return null;
      case 'ambiguous':
        this.effectiveAtError =
          'This time happens twice in Sydney: the clocks go back an hour when daylight saving ends. ' +
          'Enter a time outside the repeated hour so the effective time is unambiguous.';
        return null;
    }
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
