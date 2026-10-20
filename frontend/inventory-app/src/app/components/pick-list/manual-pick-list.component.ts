import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Category, PickListRefillRowRequest, Product } from '../../models/models';
import { PickListService } from '../../services/pick-list.service';
import { ToastService } from '../../services/toast.service';
import { generateIdempotencyKey } from './idempotency-key';
import { parsePickListRefillRejection, pickListRefillRowsEqual } from './pick-list-refill-errors';

/** The message a malformed "Taken" entry gets, matching `PickListRefillPolicy.QuantityMustBePositiveMessage`'s rule. */
export const INVALID_TAKEN_QUANTITY_MESSAGE = 'Enter a whole number greater than 0.';

export const REFILL_RECORDED_MESSAGE = 'Refill recorded.';

export const REFILL_CONFLICT_MESSAGE = 'This refill could not be recorded. Review your entries and click Save to try again.';

export const REFILL_UNCERTAIN_MESSAGE =
  "Couldn't confirm whether this refill was recorded. Review your entries and click Save to retry.";

/**
 * A blank "Taken" box is zero - the operator typed nothing for that product - and an explicit `0`
 * means the same thing, so neither is a row to submit and neither is an error. Anything else that
 * is not a positive whole number is malformed input, reported inline rather than sent to the
 * server.
 */
function parseTakenQuantity(raw: string): number | null {
  const trimmed = raw.trim();
  if (trimmed === '' || trimmed === '0') {
    return 0;
  }
  return /^\d+$/.test(trimmed) ? Number(trimmed) : null;
}

/**
 * The machine-free manual pick-list entry screen (issue #744, parent #329 child H2): a worker types
 * how many of each active product they took and saves it as one atomic, idempotent
 * `POST /api/pick-list/refills` request (issue #743). It is `PickListComponent`'s alternative to
 * the calculated matrix - offered whenever the operator chooses it, and the only choice offered at
 * all without a usable provider or the permission the calculated list needs.
 *
 * `products` is the page's own already-fetched active catalogue (the same list its Products filter
 * dropdown uses), passed in rather than fetched a second time; `refillRecorded` tells the page a
 * save just reduced storage quantities, so it can refresh that list.
 *
 * **No machine selector.** #746 (the local machine selector) has not shipped, so every submission
 * from this screen omits `machineId` - a business may pick before its machines are recorded, and
 * the batch refill contract already allows that.
 *
 * **Idempotency.** Each save keeps the rows the operator currently has typed into an exact submitted
 * row set - the products with a positive "Taken" value, in catalogue order - and compares it with
 * the last attempt. An unchanged resubmission (a retry after an uncertain failure, or simply
 * clicking Save again before changing anything) reuses the same key and the exact same row set a
 * changed one - any edited quantity, added or removed row - gets a new key, because it is a
 * different pick. Nothing here guesses which case a failure was: a `400` names what was wrong with
 * a row and nothing was recorded, a `409` means the key is spent on a different request and must
 * never be reused, and anything else (a network failure, a `5xx`) is uncertain, so the exact same
 * request is kept ready to retry rather than quietly replaced.
 *
 * **An uncertain attempt is visible until it is resolved, and this instance is what holds it.** The
 * kept rows and key only protect against a double deduction while they still exist, so an uncertain
 * outcome also raises {@link unresolvedSubmission} - an inline notice that stays on screen until a
 * later Save resolves it, rather than only the toast that disappears - and {@link saveInFlightChange}
 * tells the page a request is outstanding, which it uses to refuse switching views until it lands.
 * `PickListComponent` keeps this component alive (hidden) rather than destroying it when the other
 * view is shown, so neither the typed quantities nor the key can be lost by a mode switch; see
 * docs/architecture.md § Manual pick-list entry and mode selection.
 */
@Component({
  selector: 'app-manual-pick-list',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './manual-pick-list.component.html'
})
export class ManualPickListComponent {
  @Input() products: Product[] = [];

  /** Emitted once a save recorded a refill, so the page can refresh storage quantities. */
  @Output() readonly refillRecorded = new EventEmitter<void>();

  /** Emitted whenever a save starts or finishes, so the page can refuse a mode switch while one is outstanding. */
  @Output() readonly saveInFlightChange = new EventEmitter<boolean>();

  search = '';
  categoryId: number | '' = '';
  saving = false;

  /**
   * Set when a save ended without the server's answer - a network failure or a `5xx` - so it is not
   * known whether the refill was recorded. Stays set, and stays on screen, until a later Save
   * resolves it; the kept rows and key make that retry the identical request.
   */
  unresolvedSubmission = false;

  /** The inline wording for {@link unresolvedSubmission}; the same sentence the toast carries. */
  readonly unresolvedMessage = REFILL_UNCERTAIN_MESSAGE;

  /** Per-row refusal messages from the last save attempt, keyed by product id. */
  rowErrors = new Map<number, readonly string[]>();

  /** Refusals about the request as a whole from the last save attempt. */
  requestErrors: string[] = [];

  /** The raw text typed into each product's "Taken" box, keyed by product id. */
  private readonly takenById = new Map<number, string>();

  /** The exact rows and key of the last attempted save, kept so an unchanged retry reuses both. */
  private lastSubmission: { key: string; rows: PickListRefillRowRequest[] } | null = null;

  constructor(private readonly pickListService: PickListService, private readonly toastService: ToastService) {}

  categories(): Category[] {
    const byId = new Map<number, Category>();
    for (const product of this.products) {
      if (product.category) {
        byId.set(product.category.id, product.category);
      }
    }
    return [...byId.values()].sort((a, b) => a.name.localeCompare(b.name));
  }

  visibleProducts(): Product[] {
    const term = this.search.trim().toLowerCase();
    return this.products.filter((product) => {
      if (this.categoryId !== '' && product.category?.id !== this.categoryId) {
        return false;
      }
      return term === '' || product.name.toLowerCase().includes(term);
    });
  }

  takenValue(productId: number): string {
    return this.takenById.get(productId) ?? '';
  }

  onTakenChange(productId: number, value: string): void {
    this.takenById.set(productId, value);
    this.rowErrors.delete(productId);
  }

  /** The one message for this row: a malformed entry first, else the server's own last refusal. */
  rowError(productId: number): string | null {
    if (parseTakenQuantity(this.takenValue(productId)) === null) {
      return INVALID_TAKEN_QUANTITY_MESSAGE;
    }
    const messages = this.rowErrors.get(productId);
    return messages && messages.length > 0 ? messages.join(' ') : null;
  }

  get canSave(): boolean {
    if (this.saving) {
      return false;
    }
    const rows = this.buildRows();
    return rows !== null && rows.length > 0;
  }

  save(): void {
    const rows = this.buildRows();
    if (this.saving || rows === null || rows.length === 0) {
      return;
    }

    const reuse = this.lastSubmission !== null && pickListRefillRowsEqual(this.lastSubmission.rows, rows);
    const key = reuse ? this.lastSubmission!.key : generateIdempotencyKey();
    const submittedRows = reuse ? this.lastSubmission!.rows : rows;
    this.lastSubmission = { key, rows: submittedRows };

    this.saving = true;
    this.unresolvedSubmission = false;
    this.requestErrors = [];
    this.rowErrors.clear();
    this.saveInFlightChange.emit(true);

    this.pickListService.recordRefill({ idempotencyKey: key, rows: submittedRows }).subscribe({
      next: () => {
        this.saving = false;
        this.lastSubmission = null;
        this.takenById.clear();
        this.saveInFlightChange.emit(false);
        this.toastService.success(REFILL_RECORDED_MESSAGE);
        this.refillRecorded.emit();
      },
      error: (error: unknown) => {
        this.saving = false;
        this.saveInFlightChange.emit(false);
        this.handleSaveError(error, submittedRows);
      }
    });
  }

  /** Every row with a positive, well-formed quantity, in catalogue order; `null` while any typed entry is malformed. */
  private buildRows(): PickListRefillRowRequest[] | null {
    const rows: PickListRefillRowRequest[] = [];
    for (const product of this.products) {
      const quantity = parseTakenQuantity(this.takenValue(product.id));
      if (quantity === null) {
        return null;
      }
      if (quantity > 0) {
        rows.push({ productId: product.id, quantityTaken: quantity });
      }
    }
    return rows;
  }

  private handleSaveError(error: unknown, submittedRows: PickListRefillRowRequest[]): void {
    const rejection = parsePickListRefillRejection(error, submittedRows);
    if (rejection !== null) {
      // Nothing was recorded: ValidateRequestShape/ValidateAgainstState both refuse before the
      // idempotency key is ever claimed, so the same key is safe to resend once the operator fixes
      // the rows this refusal named - `lastSubmission` is left exactly as it is.
      this.rowErrors = rejection.rowErrors;
      this.requestErrors = [...rejection.requestErrors];
      return;
    }

    if (error instanceof HttpErrorResponse && error.status === 409) {
      // The key is spent on a different request; reusing it would only be refused again.
      this.lastSubmission = null;
      this.toastService.error(this.conflictMessage(error));
      return;
    }

    // An uncertain outcome - a network failure, a 5xx - so the exact submitted rows and key stay
    // ready: clicking Save again with nothing changed resends precisely this request. The notice
    // stays on screen until that retry resolves it, because a toast the operator missed is not a
    // reason to let an unconfirmed pick look finished.
    this.unresolvedSubmission = true;
    this.toastService.error(REFILL_UNCERTAIN_MESSAGE);
  }

  /** The 409's own detail sentence (`DomainExceptionHandler`'s ProblemDetails), falling back to a generic one. */
  private conflictMessage(error: HttpErrorResponse): string {
    const body = error.error as { detail?: unknown } | null;
    const detail = body && typeof body === 'object' ? body.detail : null;
    return typeof detail === 'string' && detail.trim().length > 0 ? detail : REFILL_CONFLICT_MESSAGE;
  }
}
