import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { StockService } from '../../../services/stock.service';
import { LocalMachine, RestockCostSuggestion, StockAdjustmentReason } from '../../../models/models';
import { SupportSessionService } from '../../../support-session/support-session.service';

/** What is shown in place of the machine field when the page's machine read failed (issue #746). */
export const ADJUSTMENT_MACHINES_ERROR =
  'The machine list could not be loaded, so an adjustment cannot be attributed to a machine.';

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
export class StockAdjustmentFormComponent implements OnChanges, OnDestroy {
  /** The product the adjustment is recorded against. */
  @Input({ required: true }) productId!: number;

  /**
   * The business's own machine records, which the page reads from `/api/local-machines` and hands
   * over (issue #746). They need no sales provider, so the field works for a business that has
   * never connected one.
   *
   * The list is every machine the business has recorded, active and inactive alike; **only the
   * active ones are offered**, because this form records a new movement and a retired machine is
   * not somewhere stock is being put now. An **empty** offer is a business with no active machine:
   * the field is hidden rather than offering "— None —" alone. A read that failed or is still
   * outstanding is a different state and arrives as {@link machinesFailed}/{@link machinesLoading},
   * never as an empty list.
   */
  @Input() machines: LocalMachine[] = [];

  /**
   * Whether the page's machine read failed. The field is then replaced by an explicit error rather
   * than hidden: "there are no machines" is a claim about the business that a failed read did not
   * make (issue #746, keeping issue #728's error-versus-empty rule).
   */
  @Input() machinesFailed = false;

  /** Whether that read is still outstanding; the field says so rather than reading as empty. */
  @Input() machinesLoading = false;

  /** Raised after the existing adjustment endpoint accepted a movement, so the page can reload. */
  @Output() readonly adjustmentApplied = new EventEmitter<void>();

  readonly StockAdjustmentReason = StockAdjustmentReason;
  error = '';
  saving = false;
  restockCostSuggestion: RestockCostSuggestion | null = null;
  private restockCostRequired = false;

  /**
   * Whether a support session is active (issue #658): Apply Adjustment is disabled while it is,
   * because a session grants `InventoryOperate` - the capability this write would otherwise be
   * gated on - and the backend refuses the write itself only by its read-only method rule, never
   * by withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;

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

  constructor(private readonly stockService: StockService, supportSession: SupportSessionService) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  /**
   * A different product is a different adjustment: the in-progress quantity, reason, cost and the
   * cost suggestion of the previous product must not carry over to it.
   */
  ngOnChanges(changes: SimpleChanges): void {
    if (changes['productId'] && !changes['productId'].firstChange) {
      this.reset();
    }
    // A machine the field no longer offers must not stay staged on the form: the list may have
    // gone away entirely, or that machine may since have been deactivated.
    if (changes['machines'] || changes['machinesFailed'] || changes['machinesLoading']) {
      this.dropUnofferedMachine();
    }
  }

  /**
   * The machines an adjustment can name: the business's **active** machines, keyed by their own
   * `LocalMachine.id` - the id a stock movement records (issue #741) and the API accepts.
   */
  get attributableMachines(): LocalMachine[] {
    return this.machines.filter((machine) => machine.isActive);
  }

  /** Whether a machine may be attributed at all; see {@link machines}. */
  get machineFieldShown(): boolean {
    return !this.machinesFailed && !this.machinesLoading && this.attributableMachines.length > 0;
  }

  /** The sentence shown in place of the field when the machine read failed. */
  get machinesError(): string {
    return this.machinesFailed ? ADJUSTMENT_MACHINES_ERROR : '';
  }

  private dropUnofferedMachine(): void {
    if (this.form.machineId === null) return;
    const offered =
      this.machineFieldShown && this.attributableMachines.some((machine) => machine.id === this.form.machineId);
    if (!offered) {
      this.form.machineId = null;
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
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) {
      return;
    }
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
