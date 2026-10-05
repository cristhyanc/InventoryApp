import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { StockService } from '../../../services/stock.service';
import { Machine, RestockCostSuggestion, StockAdjustmentReason } from '../../../models/models';

/**
 * The manual stock-adjustment workflow for one selected product (issue #384), moved out of the
 * former `/products/:id/stock` page component unchanged.
 *
 * It is a dedicated feature component rather than markup on the Stock History page because it is a
 * distinct workflow with its own state, its own validation and its own API calls - the restock-cost
 * suggestion lookup, the correction sign handling, the required restock unit cost and the server's
 * validation errors (see docs/architecture.md § Page composition boundary). The page supplies the
 * product and the machine list through inputs and only learns that an adjustment was applied, so it
 * can refresh the history.
 *
 * It creates no new kind of stock movement: it posts to the existing
 * `POST /api/products/{productId}/stock` endpoint, which stays the sole authority on costing,
 * insufficient-stock validation and reason/source semantics. The client-side checks here are the
 * same ones the product page already made, and they only stop an obviously invalid request early.
 */
@Component({
  selector: 'app-stock-adjustment-form',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './stock-adjustment-form.component.html'
})
export class StockAdjustmentFormComponent implements OnChanges {
  /** The product the adjustment is recorded against. */
  @Input({ required: true }) productId!: number;

  /** The machines an adjustment may be attributed to; the page already loads them for its filters. */
  @Input() machines: Machine[] = [];

  /** Raised after the existing adjustment endpoint accepted a movement, so the page can reload. */
  @Output() readonly adjustmentApplied = new EventEmitter<void>();

  readonly StockAdjustmentReason = StockAdjustmentReason;
  error = '';
  saving = false;
  restockCostSuggestion: RestockCostSuggestion | null = null;
  private restockCostRequired = false;

  form = {
    quantityChange: null as number | null,
    reason: StockAdjustmentReason.Restock,
    machineId: null as number | null,
    notes: '',
    EatBefore: null as string | null,
    unitCost: null as number | null
  };

  reasonOptions = [
    { value: StockAdjustmentReason.Restock, label: 'Restock' },
    { value: StockAdjustmentReason.Sale, label: 'Sale' },
    { value: StockAdjustmentReason.Damaged, label: 'Damaged' },
    { value: StockAdjustmentReason.Expired, label: 'Expired' },
    { value: StockAdjustmentReason.Correction, label: 'Correction' },
    { value: StockAdjustmentReason.MachineRefill, label: 'Machine refill' }
  ];

  constructor(private stockService: StockService) {}

  /**
   * A different product is a different adjustment: the in-progress quantity, reason, cost and the
   * cost suggestion of the previous product must not carry over to it.
   */
  ngOnChanges(changes: SimpleChanges): void {
    if (changes['productId'] && !changes['productId'].firstChange) {
      this.reset();
    }
  }

  get requiresRestockCost(): boolean {
    return this.form.reason === StockAdjustmentReason.Restock && (this.form.quantityChange ?? 0) > 0;
  }

  get isCorrection(): boolean {
    return this.form.reason === StockAdjustmentReason.Correction;
  }

  get restockCostHelp(): string {
    if (this.restockCostSuggestion?.source === 'LastPurchase' && this.restockCostSuggestion.unitCost !== null) {
      return `Last purchase cost: $${this.restockCostSuggestion.unitCost.toFixed(2)}`;
    }
    if (this.restockCostSuggestion?.source === 'AverageUnitCost' && this.restockCostSuggestion.unitCost !== null) {
      return `Using current average cost: $${this.restockCostSuggestion.unitCost.toFixed(2)} - verify before saving`;
    }
    return 'No previous purchase cost is available. Enter the actual unit cost.';
  }

  onAdjustmentInputsChanged(): void {
    if (this.isCorrection && this.form.quantityChange !== null) {
      this.form.quantityChange = Math.abs(this.form.quantityChange);
    }

    if (this.requiresRestockCost && !this.restockCostRequired) {
      this.restockCostRequired = true;
      this.stockService.restockCostSuggestion(this.productId).subscribe({
        next: (suggestion) => {
          this.restockCostSuggestion = suggestion;
          this.form.unitCost = suggestion.unitCost;
        },
        error: () => {
          this.restockCostSuggestion = null;
          this.form.unitCost = null;
        }
      });
      return;
    }

    if (!this.requiresRestockCost) {
      this.restockCostRequired = false;
      this.restockCostSuggestion = null;
      this.form.unitCost = null;
    }
  }

  adjust(): void {
    const quantityChange = this.isCorrection
      ? -(Math.abs(this.form.quantityChange ?? 0))
      : this.form.quantityChange;
    if (quantityChange === null || quantityChange === 0) {
      this.error = this.isCorrection
        ? 'Enter a positive quantity to remove.'
        : 'Enter a non-zero quantity (use negative numbers to remove stock).';
      return;
    }
    const unitCost = this.form.unitCost;
    if (this.requiresRestockCost) {
      if (unitCost === null || unitCost === undefined) {
        this.error = 'Unit cost is required for a positive Restock adjustment.';
        return;
      }
      if (unitCost < 0) {
        this.error = 'Unit cost cannot be negative for a positive Restock adjustment.';
        return;
      }
    }
    this.error = '';
    this.saving = true;

    const payload = {
      ...this.form,
      quantityChange,
      unitCost: this.requiresRestockCost ? this.form.unitCost : null
    };
    this.stockService.adjust(this.productId, payload).subscribe({
      next: () => {
        this.reset();
        this.saving = false;
        this.adjustmentApplied.emit();
      },
      error: (err) => {
        // The insufficient-stock/validation failures behind this action moved from a plain
        // string body to a ProblemDetails object (issue #59); handle both shapes, matching the
        // same fallback already used in machine-detail.component.ts.
        const body = err?.error;
        this.error = typeof body === 'string' ? body : (body?.message ?? body?.title ?? 'Failed to adjust stock.');
        this.saving = false;
      }
    });
  }

  private reset(): void {
    this.form = {
      quantityChange: null,
      reason: StockAdjustmentReason.Restock,
      machineId: null,
      notes: '',
      EatBefore: '',
      unitCost: null
    };
    this.restockCostRequired = false;
    this.restockCostSuggestion = null;
    this.error = '';
  }
}
