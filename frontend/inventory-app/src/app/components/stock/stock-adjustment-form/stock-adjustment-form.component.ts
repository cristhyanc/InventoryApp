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
 * The reasons that may only remove stock (issue #1242). The operator types a positive quantity for
 * each of them and the form sends it negative, the way Correction alone used to.
 */
export const REMOVAL_REASONS: readonly StockAdjustmentReason[] = [
  StockAdjustmentReason.Correction,
  StockAdjustmentReason.Damaged,
  StockAdjustmentReason.Expired,
  StockAdjustmentReason.Sale
];

/**
 * What the operator is told when a Restock would remove stock (issue #1242). The same sentence
 * `ManualStockAdjustmentPolicy.RestockAddsStockMessage` answers the request with, so the form
 * refuses it in the same words the endpoint would.
 */
export const RESTOCK_ADDS_STOCK_MESSAGE =
  'A Restock adjustment must add stock: enter a positive quantity. ' +
  'Use Correction, Damaged or Expired to remove stock.';

/** What the operator is told when Apply is reached with no reason chosen (issue #1242). */
export const REASON_REQUIRED_MESSAGE = 'Select a reason for this adjustment.';

/**
 * Which way a Machine refill moves stock (issue #1331). The stored movement is still signed from
 * storage's point of view - negative out to a machine, positive for units returned - but the
 * operator chooses the direction in words instead of typing the sign, because "Machine refill 5"
 * typed the natural way recorded a *return*: storage went up by 5 and the machine's expected stock
 * went down by 5.
 */
export type MachineRefillDirection = 'IntoMachine' | 'BackToStorage';

/** The direction a Machine refill opens with: the common one, storage into the machine. */
export const DEFAULT_MACHINE_REFILL_DIRECTION: MachineRefillDirection = 'IntoMachine';

/** The two directions, in the order they are offered. */
export const MACHINE_REFILL_DIRECTIONS: readonly { value: MachineRefillDirection; label: string }[] = [
  { value: 'IntoMachine', label: 'Into the machine (from storage)' },
  { value: 'BackToStorage', label: 'Back to storage (from the machine)' }
];

/**
 * What the operator is told when a Machine refill names no machine (issue #1331). The endpoint
 * refuses the same request through `ManualStockAdjustmentPolicy.MachineRequiredMessage`; this asks
 * for the field by the name the form gives it.
 */
export const REFILL_MACHINE_REQUIRED_MESSAGE =
  'Choose the machine this refill moves stock to or from.';

/**
 * The manual stock-adjustment workflow for one selected product (issue #384), moved out of the
 * former `/products/:id/stock` page component unchanged.
 *
 * It is a dedicated feature component rather than markup on the Stock History page because it is a
 * distinct workflow with its own state, its own validation and its own API calls - the restock-cost
 * suggestion lookup, each reason's own sign handling (issue #1242), a Machine refill's direction
 * and required machine (issue #1331), the required restock unit cost and the server's validation
 * errors (see docs/architecture.md § Page composition boundary). The
 * page supplies the product and the machine list through inputs and only learns that an adjustment
 * was applied, so it can refresh the history. Since issue #1156 it is shown inside `StockAdjustmentPanelComponent`, the
 * Adjust stock drawer (a full-screen form on a phone), and carries that panel's Cancel and Apply
 * adjustment actions.
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
  templateUrl: './stock-adjustment-form.component.html',
  // Fills the Adjust stock panel below its heading, so the fields scroll and the actions stay put.
  host: { class: 'flex min-h-0 flex-1 flex-col' }
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

  /**
   * Raised by Cancel (issue #1156): the form is shown in the Adjust stock panel, which closes on it.
   * Cancel is refused while an adjustment is being saved, so a write in flight is never abandoned
   * behind a closed panel.
   */
  @Output() readonly cancelled = new EventEmitter<void>();

  /** Reports whether an adjustment is being saved, so the panel around it can refuse to close. */
  @Output() readonly savingChange = new EventEmitter<boolean>();

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

  /**
   * The form opens with **no reason chosen** (issue #1242): a default of Restock made the reason a
   * field an operator could leave alone, so a quantity typed for a write-off was recorded as a
   * restock of that many units.
   */
  form = {
    quantityChange: null as number | null,
    reason: null as StockAdjustmentReason | null,
    machineId: null as number | null,
    notes: '',
    EatBefore: null as string | null,
    unitCost: null as number | null
  };

  /**
   * The chosen direction of a Machine refill, or `null` under every other reason (issue #1331).
   *
   * It is deliberately **not** a field of {@link form}: it decides the sign of the quantity that is
   * submitted and is never part of the request itself, and keeping it out of the form object is what
   * makes that true of every submission rather than of one hand-written payload.
   */
  refillDirection: MachineRefillDirection | null = null;

  readonly refillDirections = MACHINE_REFILL_DIRECTIONS;

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

  /**
   * Whether the movement can be attributed to a machine at all (issue #1156): only a **Machine
   * refill** moves stock to a machine, so the field is disabled for every other reason and a
   * machine chosen under Machine refill is dropped when the reason changes away from it, rather
   * than being submitted with a Restock, Sale, Damaged, Expired or Correction movement.
   */
  get machineApplicable(): boolean {
    return this.form.reason === StockAdjustmentReason.MachineRefill;
  }

  /**
   * The machine field's label. A Machine refill **requires** a machine (issue #1331): the refill is
   * a transfer between storage and one named machine, and without the machine only storage's half of
   * it was recorded - the machine stock ledger reads the refills that named one and never saw the
   * others. Under every other reason the field records nothing and is disabled, so it is labelled
   * neither required nor optional.
   */
  get machineFieldLabel(): string {
    return this.machineApplicable ? 'Machine *' : 'Machine';
  }

  /**
   * What each direction does, in words, for the quantity typed (issue #1331), so the operator reads
   * the effect of the movement instead of inferring it from a sign.
   */
  get refillDirectionHint(): string {
    const units = Math.abs(this.form.quantityChange ?? 0);
    const amount = units > 0 ? `${units}` : 'that number';
    return this.refillDirection === 'BackToStorage'
      ? `Storage goes up by ${amount} and the machine goes down by ${amount}.`
      : `Storage goes down by ${amount} and the machine goes up by ${amount}.`;
  }

  get requiresRestockCost(): boolean {
    return this.form.reason === StockAdjustmentReason.Restock && (this.form.quantityChange ?? 0) > 0;
  }

  /**
   * Whether the chosen reason may only remove stock (issue #1242): the quantity is then typed as a
   * positive number of units and submitted negative.
   */
  get removesStock(): boolean {
    return this.form.reason !== null && REMOVAL_REASONS.includes(this.form.reason);
  }

  /** Whether the chosen reason may only add stock; a Restock is the one that does (issue #1242). */
  get addsStock(): boolean {
    return this.form.reason === StockAdjustmentReason.Restock;
  }

  /**
   * `1` once a reason is chosen, so the field itself refuses a wrong sign. Since issue #1331 a
   * Machine refill is counted in positive units too, with {@link refillDirection} deciding the sign.
   */
  get quantityMinimum(): number | null {
    return this.removesStock || this.addsStock || this.machineApplicable ? 1 : null;
  }

  /**
   * What the operator is told when the quantity is missing or zero: the chosen reason decides which
   * direction is being asked for (issue #1242), so the sentence belongs with the direction getters
   * above rather than inside {@link adjust}.
   */
  private get quantityRequiredMessage(): string {
    if (this.removesStock) {
      return 'Enter a positive quantity to remove.';
    }
    if (this.addsStock) {
      return 'Enter a positive quantity to add.';
    }
    if (this.machineApplicable) {
      return 'Enter a positive quantity to move.';
    }
    return 'Enter a non-zero quantity.';
  }

  /**
   * The quantity change the request carries: the number of units typed, signed by the chosen
   * reason. A removal reason submits it negative (issue #1242) and a Machine refill submits it
   * negative into the machine and positive back to storage (issue #1331), which is the same stored
   * meaning the movement always had - the sign is storage's point of view.
   */
  private get submittedQuantityChange(): number | null {
    const typed = this.form.quantityChange;
    if (this.removesStock) {
      return -Math.abs(typed ?? 0);
    }
    if (this.machineApplicable) {
      const units = Math.abs(typed ?? 0);
      return this.refillDirection === 'BackToStorage' ? units : -units;
    }
    return typed;
  }

  /** A reason is required before anything can be saved (issue #1242). */
  get reasonChosen(): boolean {
    return this.form.reason !== null;
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

  /**
   * Why the staged unit cost is refused, or `''` when the movement may be sent: a positive Restock
   * needs a present, non-negative cost and every other movement carries none. The endpoint remains
   * the authority; this keeps the rule in one place instead of nested inside {@link adjust}.
   */
  private get restockCostError(): string {
    if (!this.requiresRestockCost) {
      return '';
    }
    const unitCost = this.form.unitCost;
    if (unitCost === null || unitCost === undefined) {
      return 'Unit cost is required for a positive Restock adjustment.';
    }
    if (unitCost < 0) {
      return 'Unit cost cannot be negative for a positive Restock adjustment.';
    }
    return '';
  }

  onAdjustmentInputsChanged(): void {
    if (!this.machineApplicable) {
      this.form.machineId = null;
    }

    // The direction belongs to a Machine refill and to no other reason (issue #1331): choosing the
    // reason offers it, already set to the common direction, and choosing another reason clears it
    // so a stale choice cannot sign the next movement.
    if (!this.machineApplicable) {
      this.refillDirection = null;
    } else if (this.refillDirection === null) {
      this.refillDirection = DEFAULT_MACHINE_REFILL_DIRECTION;
    }

    // A reason that removes stock, and a Machine refill, take the quantity as a positive number of
    // units: the field shows what was typed without its sign and the submission applies the
    // direction (issues #1242 and #1331), so a typed "-5" is never negated twice.
    if ((this.removesStock || this.machineApplicable) && this.form.quantityChange !== null) {
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
    // One write at a time (issue #1156): a second press or Enter while the first is in flight is
    // ignored rather than recording the same movement twice.
    if (this.saving) {
      return;
    }
    // Checked here as well as disabled in the template, for the same reason the session is.
    const reason = this.form.reason;
    if (reason === null) {
      this.error = REASON_REQUIRED_MESSAGE;
      return;
    }
    const quantityChange = this.submittedQuantityChange;
    if (quantityChange === null || quantityChange === 0) {
      this.error = this.quantityRequiredMessage;
      return;
    }
    // A Restock only ever adds stock, and the endpoint refuses one that does not in these words
    // (issue #1242).
    if (this.addsStock && quantityChange < 0) {
      this.error = RESTOCK_ADDS_STOCK_MESSAGE;
      return;
    }
    // A refill is a transfer between storage and one machine, so the machine is required and the
    // endpoint refuses a refill without one (issue #1331).
    if (this.machineApplicable && this.form.machineId === null) {
      this.error = REFILL_MACHINE_REQUIRED_MESSAGE;
      return;
    }
    const restockCostError = this.restockCostError;
    if (restockCostError !== '') {
      this.error = restockCostError;
      return;
    }
    this.error = '';
    this.setSaving(true);

    const payload = {
      ...this.form,
      reason,
      quantityChange,
      machineId: this.machineApplicable ? this.form.machineId : null,
      unitCost: this.requiresRestockCost ? this.form.unitCost : null
    };
    this.stockService.adjust(this.productId, payload).subscribe({
      next: () => {
        this.reset();
        this.setSaving(false);
        this.adjustmentApplied.emit();
      },
      error: (err) => {
        // The insufficient-stock/validation failures behind this action moved from a plain
        // string body to a ProblemDetails object (issue #59); handle both shapes, matching the
        // same fallback already used in machine-detail.component.ts.
        const body = err?.error;
        // Everything the operator entered stays on the form, so the message can be acted on and
        // the adjustment applied again without retyping it.
        this.error = typeof body === 'string' ? body : (body?.message ?? body?.title ?? 'Failed to adjust stock.');
        this.setSaving(false);
      }
    });
  }

  cancel(): void {
    if (this.saving) {
      return;
    }
    this.cancelled.emit();
  }

  private setSaving(saving: boolean): void {
    this.saving = saving;
    this.savingChange.emit(saving);
  }

  private reset(): void {
    this.form = {
      quantityChange: null,
      reason: null,
      machineId: null,
      notes: '',
      EatBefore: '',
      unitCost: null
    };
    this.refillDirection = null;
    this.restockCostRequired = false;
    this.restockCostSuggestion = null;
    this.error = '';
  }
}
