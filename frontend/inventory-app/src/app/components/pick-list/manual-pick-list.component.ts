import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, EventEmitter, Input, OnDestroy, OnInit, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable, Subscription } from 'rxjs';
import {
  Category,
  LocalMachine,
  MultiMachinePickListRefillRequest,
  MultiMachineRefillAllocationRequest,
  PickListRefillRequest,
  PickListRefillRowRequest,
  Product
} from '../../models/models';
import { LocalMachineService } from '../../services/local-machine.service';
import { PickListService } from '../../services/pick-list.service';
import { ToastService } from '../../services/toast.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { IconComponent } from '../shared/icon.component';
import { MultiSelectDropdownComponent, MultiSelectOption } from '../shared/multi-select-dropdown.component';
import { generateIdempotencyKey } from './idempotency-key';
import {
  multiMachineAllocationsEqual,
  parseMultiMachineRefillRejection,
  parsePickListRefillRejection,
  pickListRefillRowsEqual,
  refillCellKey
} from './pick-list-refill-errors';

/** The message a malformed "Taken" entry gets, matching `PickListRefillPolicy.QuantityMustBePositiveMessage`'s rule. */
export const INVALID_TAKEN_QUANTITY_MESSAGE = 'Enter a whole number greater than 0.';

/**
 * The largest quantity one entry may hold: the server records a quantity as a 32-bit integer, so a
 * bigger number is refused here, naming the cell it was typed into, rather than by model binding
 * with a message about a JSON field nobody typed.
 */
export const MAXIMUM_TAKEN_QUANTITY = 2147483647;

export const TAKEN_QUANTITY_TOO_LARGE_MESSAGE = `Enter a quantity of ${MAXIMUM_TAKEN_QUANTITY} or less.`;

export const REFILL_RECORDED_MESSAGE = 'Refill recorded.';

/** What a recorded multi-machine batch reports: the whole run, not one machine of it (issue #904). */
export function multiMachineRefillRecordedMessage(allocationCount: number, machineCount: number): string {
  const entries = allocationCount === 1 ? 'entry' : 'entries';
  const machines = machineCount === 1 ? 'machine' : 'machines';
  return `Refill recorded: ${allocationCount} ${entries} across ${machineCount} ${machines}.`;
}

export const REFILL_CONFLICT_MESSAGE = 'This refill could not be recorded. Review your entries and click Record refill to try again.';

export const REFILL_UNCERTAIN_MESSAGE =
  "Couldn't confirm whether this refill was recorded. Use Retry save to resend exactly the same refill; your entries stay locked until it is confirmed.";

export const REFILL_FORBIDDEN_MESSAGE = 'This refill was refused, so nothing was recorded.';

export const SUPPORT_SESSION_READ_ONLY_MESSAGE =
  'A support session is active, so quantities taken cannot be recorded here. End the session to record a refill.';

export const CATALOGUE_LOAD_FAILED_MESSAGE = 'The product list could not be loaded, so quantities taken cannot be entered.';

export const CATALOGUE_REFRESH_FAILED_MESSAGE =
  'Storage quantities could not be refreshed after the last save, so the numbers shown may be out of date.';

export const MACHINES_LOAD_FAILED_MESSAGE =
  'The machine list could not be loaded, so this refill cannot be attributed to a machine.';

export const MACHINE_ATTRIBUTION_FAILED_MESSAGE =
  'The machine this entry was opened for could not be selected automatically - it may have been deactivated or removed. Choose it below if it is still listed, or record without a machine.';

export const MACHINE_RESOLUTION_PENDING_MESSAGE =
  'Confirming the machine this entry was opened for. Quantities can be typed now and recorded once it is confirmed.';

/**
 * What the operator is asked before the "No machine" column's quantities are dropped on the way into
 * machine columns (issue #904). They cannot be carried over: a quantity typed against no machine
 * says nothing about which machine it was walked to, and copying it into every selected column would
 * invent a pick several times the size of the one that was typed.
 */
export const DISCARD_UNATTRIBUTED_ENTRIES_CONFIRMATION =
  'Selecting machines replaces the single Quantity taken column with one column per machine. The quantities already typed cannot be attributed to a machine, so they will be discarded. Continue?';

/** What the operator is asked before every machine column's quantities are dropped on the way back to "No machine". */
export const DISCARD_MACHINE_COLUMNS_CONFIRMATION =
  'Clearing the machine selection replaces the machine columns with a single Quantity taken column. The quantities typed per machine will be discarded. Continue?';

/** What the operator is asked before one or more machine columns that hold quantities are removed. */
export function discardMachineColumnConfirmation(...machineNames: string[]): string {
  const columns = machineNames.length === 1 ? 'column' : 'columns';
  return `Remove the ${machineNames.join(', ')} ${columns}? The quantities already typed there will be discarded.`;
}

/** The sentence introducing every entry that has to be fixed before Record refill is offered again. */
export const INVALID_ENTRIES_PREFIX = 'Fix these entries before recording:';

/** What the operator is asked before Clear drops every entered quantity. */
export const CLEAR_ENTRIES_CONFIRMATION = 'Clear every entered quantity, including any hidden by the current filters?';

/** The primary action's label while entered totals exceed known storage: recording is blocked until they are resolved. */
export const RESOLVE_SHORTAGE_LABEL = 'Resolve shortage to record';

/** What a confirmed recording reports on screen: the totals the server confirmed, never before. */
export function refillRecordedSummary(units: number, machineCount: number): string {
  const unitWord = units === 1 ? 'unit' : 'units';
  if (machineCount === 0) {
    return `Refill recorded: ${units} ${unitWord} taken from storage.`;
  }
  return `Refill recorded: ${units} ${unitWord} across ${machineCount} ${machineCount === 1 ? 'machine' : 'machines'}.`;
}

/** Which rows the entry table shows, beside the search and category filters. */
export type RefillEntryFilter = 'all' | 'entered' | 'shortages';

/**
 * Where the page's read of the active catalogue stands. Only `'loaded'` means {@link
 * ManualPickListComponent.products} is a real answer, so only then may an empty list be described
 * as "no products".
 */
export type ManualPickListCatalogueState = 'loading' | 'failed' | 'loaded';

/** A parsed entry: a quantity (`0` for "nothing taken") or the message the entry is refused with. */
type ParsedTakenEntry = { readonly quantity: number } | { readonly error: string };

/**
 * A blank "Taken" box is zero - the operator typed nothing there - and an explicit `0` means the
 * same thing, so neither is something to submit and neither is an error. Anything else that is not a
 * positive whole number the server can record is malformed input, reported inline rather than sent.
 */
function parseTakenEntry(raw: string): ParsedTakenEntry {
  const trimmed = raw.trim();
  if (trimmed === '' || trimmed === '0') {
    return { quantity: 0 };
  }
  if (!/^\d+$/.test(trimmed)) {
    return { error: INVALID_TAKEN_QUANTITY_MESSAGE };
  }
  const quantity = Number(trimmed);
  return quantity > MAXIMUM_TAKEN_QUANTITY ? { error: TAKEN_QUANTITY_TOO_LARGE_MESSAGE } : { quantity };
}

/** Whether an entry holds something the operator would mind losing; an explicit `0` is nothing. */
function holdsQuantity(raw: string | undefined): boolean {
  const trimmed = (raw ?? '').trim();
  return trimmed !== '' && trimmed !== '0';
}

/**
 * Exactly what one Save sends, and the one thing an uncertain retry resends. The two shapes are the
 * two endpoints: an unattributed pick is the single-machine batch endpoint with no machine at all
 * (issue #743), and a pick with machine columns is the multi-machine batch (issue #897).
 */
type ManualPickPayload =
  | { readonly kind: 'unattributed'; readonly rows: PickListRefillRowRequest[] }
  | { readonly kind: 'batch'; readonly allocations: MultiMachineRefillAllocationRequest[] };

/** One attempted submission: its idempotency key and the exact payload that key stands for. */
interface ManualPickSubmission {
  readonly key: string;
  readonly payload: ManualPickPayload;
}

/**
 * The manual pick-list entry screen (issue #744, parent #329 child H2, extended to a machine matrix
 * by issue #904): a worker types how many of each active product they took and saves the whole pick
 * as one atomic, idempotent request. It is `PickListComponent`'s alternative to the calculated
 * matrix - offered whenever the operator chooses it, and the only choice offered at all without a
 * usable provider or the permission the calculated list needs.
 *
 * `products` is the page's own already-fetched active catalogue (the same list its Products filter
 * dropdown uses), passed in rather than fetched a second time; `refillRecorded` tells the page a
 * save just reduced storage quantities, so it can refresh that list.
 *
 * **Two modes, one per endpoint, chosen by the machine selection.** The machine list comes from
 * `LocalMachineService.getAll()` and offers only the business's **active** local machines, labelled
 * by {@link LocalMachine.name} - never a provider machine number, and never a Nayax call.
 *
 * - **No machine selected** is the machine-free entry issue #744 shipped, unchanged: one "Taken"
 *   column, saved as unattributed refills through `POST /api/pick-list/refills` with no `machineId`
 *   at all. A business with no active machine, or whose machine read failed
 *   ({@link machinesLoadFailed}), has only this mode, exactly as before.
 * - **One or more machines selected** gives each machine its own quantity column, in selection
 *   order, and one Save sends the whole run as a single `POST /api/pick-list/multi-machine-refills`
 *   batch (issue #897) - never one request per machine, which is what that endpoint exists to
 *   replace: only one request can weigh each product's **total** across the machines it was
 *   allocated to against the one shelf it comes off. The row shows Product, Category, Storage Qty,
 *   Total Taken - the row's own sum across the selected machines - and then the machine columns.
 *
 * **Drafts are per mode and are never silently reassigned.** The "No machine" entries and the matrix
 * cells are separate drafts, and nothing copies one into the other: a generic quantity is never
 * written into every selected machine, because that would invent a pick several times the size of
 * the one that was typed. A selection change that would drop entries therefore asks first
 * ({@link DISCARD_UNATTRIBUTED_ENTRIES_CONFIRMATION}, {@link DISCARD_MACHINE_COLUMNS_CONFIRMATION},
 * {@link discardMachineColumnConfirmation}) and Cancel changes nothing at all - neither the
 * selection nor a single entry. The search and category filters never touch a draft: they hide rows,
 * and a hidden row's entries are still part of the pick, still validated and still counted in
 * {@link totalUnitsRequired}, which is why {@link hiddenUnitsRequired} says so on screen.
 *
 * **A machine can also arrive preselected, from the machine-details page's "Record refill" link**
 * (issue #744's machine-page consolidation). `PickListComponent` reads the link's `machineId` query
 * parameter - already the local machine id `MachineDetailComponent` resolved for that page, never
 * the raw Nayax number - and hands it in as {@link preselectedMachineId}. Once the machine read
 * settles it is accepted only if it names one of this business's own **active** machines; if it
 * does, it becomes the one selected column and more machines may be added beside it. If it does not
 * - the machine was deactivated or removed after the link was opened, or the machine read itself
 * failed - {@link machineAttributionFailed} is set instead of silently leaving the selection empty:
 * the link promised this refill would be attributed to that machine, so failing to do so has to be
 * visible rather than indistinguishable from an operator who deliberately chose no machine.
 * **Until that read settles, Save is refused** ({@link machineResolutionPending}): the catalogue can
 * answer first, so an operator could otherwise type quantities and submit an unattributed refill
 * while the link's own machine was still being confirmed. A quantity typed in that window belongs to
 * that one machine - the pending notice says so - so it moves into that machine's column when the
 * read answers, rather than being dropped or sent unattributed. An entry with no linked machine at
 * all never waits.
 *
 * **Idempotency, and the rule an uncertain outcome adds.** Each save keeps the exact payload it sent
 * and the key it sent it under ({@link lastSubmission}). An unchanged resubmission - including one
 * whose machine columns were merely reordered, since the server fingerprints a batch in its own
 * machine-then-product order - reuses that key and resends that payload; a different pick gets a new
 * key. A `400` names what was wrong and recorded nothing, so the same key stays safe to resend once
 * the named cells are fixed; a `409` means the key is spent on a different request and must never be
 * reused; a `403` is a definite refusal.
 *
 * **While an outcome is uncertain - a network failure or a `5xx` - no new key may be minted at all.**
 * It is not known whether the server recorded the batch, so the only request this component will
 * send is the one it already sent: {@link retry} (and Save, which becomes that retry) resends the
 * kept payload under the kept key, and an edited cell, a removed column or a reordered selection
 * cannot change either. That is what makes the retry safe: if the first attempt did commit, the
 * server replays it and nothing is deducted twice; if it did not, the retry records it. The
 * uncertain attempt also stays on screen as {@link unresolvedSubmission} until a later attempt
 * resolves it, rather than only as a toast that fades, and {@link saveInFlightChange} tells the page
 * a request is outstanding, which it uses to refuse switching views until it lands.
 * `PickListComponent` keeps this component alive (hidden) rather than destroying it when the other
 * view is shown, so neither the typed quantities nor the key can be lost by a mode switch; see
 * docs/architecture.md § Manual pick-list entry and mode selection.
 *
 * **Nothing moves while a save is in flight.** {@link saving} disables every entry box and blocks a
 * selection change, a column removal and a cell edit outright, so the request that is being answered
 * is the request that was built.
 *
 * **A support session is read-only here too.** A session grants `Inventory.Operate`, so the backend
 * refuses this write only by its read-only method rule; like `MachineDetailComponent`'s Restock, the
 * entry boxes and Save are hidden while {@link sessionActive} is set, and {@link save} refuses on
 * its own as well, so a session that starts after the page opened cannot send a refill either. A
 * `403` that does reach here is a definite refusal - nothing was recorded - and is reported as one,
 * never as the uncertain outcome that asks for a retry.
 *
 * **Issue #996 redesigned it as Record refill** without changing what it sends. Each row shows the
 * storage available, the entries, the row's total and its status; the whole submission's totals
 * sit under the table. **Storage excess is checked across every selected machine at once** -
 * entries a filter hides included, because they all come off one shelf - and while any product's
 * total exceeds the storage it is known to hold, a fresh recording is blocked ("Resolve shortage to
 * record"). The server stays authoritative for stock that changed meanwhile, and an unconfirmed
 * retry is never blocked by it. Being below a known suggestion ({@link suggestions}) is information
 * only; no suggestion is shown, or invented, where none is known. Clear asks first, a confirmed
 * recording reports its totals ({@link recordedSummary}) only once the server has answered, and
 * **while an outcome is uncertain every entry is locked** ({@link entriesLocked}): the one action
 * offered is Retry save. On a phone ({@link compact}) the rows become cards with one focused machine
 * and touch steppers; the draft behind them is the same.
 *
 * **The catalogue's own state is the page's.** {@link catalogueState} says whether `products` is a
 * real answer yet, so a slow or failed read is never shown as an empty catalogue, and
 * {@link catalogueRefreshFailed} says the refresh after a save failed, so stale storage quantities
 * are labelled as such; both offer {@link retryCatalogue}.
 */
@Component({
  selector: 'app-manual-pick-list',
  standalone: true,
  imports: [CommonModule, FormsModule, MultiSelectDropdownComponent, IconComponent],
  templateUrl: './manual-pick-list.component.html'
})
export class ManualPickListComponent implements OnInit, OnDestroy {
  @Input() products: Product[] = [];

  /** Whether {@link products} is a real answer yet; see {@link ManualPickListCatalogueState}. */
  @Input() catalogueState: ManualPickListCatalogueState = 'loaded';

  /** Set when the catalogue refresh after a recorded refill failed, so the storage quantities shown may be stale. */
  @Input() catalogueRefreshFailed = false;

  /**
   * The local machine id a "Record refill" link named, or `null` for ordinary pick-list entry
   * (issue #744's machine-page consolidation). Resolved once, against the machine read this
   * component already makes; see {@link machineAttributionFailed} and the class doc.
   */
  @Input() preselectedMachineId: number | null = null;

  /**
   * Known suggested quantities, keyed by {@link refillCellKey} of a product and a **local** machine
   * (issue #996). Only ever information beside an entry - never a value typed into one - and absent
   * for any pair nobody suggested, in which case nothing is shown rather than an invented number.
   */
  @Input() suggestions: ReadonlyMap<string, number> = new Map();

  /** Whether the page is in its phone layout: product cards and one focused machine instead of the matrix. */
  @Input() compact = false;

  /** Emitted when the operator asks to read the catalogue again after a failed load or refresh. */
  @Output() readonly retryCatalogue = new EventEmitter<void>();

  /** Emitted once a save recorded a refill, so the page can refresh storage quantities. */
  @Output() readonly refillRecorded = new EventEmitter<void>();

  /** Emitted whenever a save starts or finishes, so the page can refuse a mode switch while one is outstanding. */
  @Output() readonly saveInFlightChange = new EventEmitter<boolean>();

  search = '';
  categoryId: number | '' = '';
  entryFilter: RefillEntryFilter = 'all';
  saving = false;

  /** The machine a phone's cards take entries for; `null` falls back to the first selected machine. */
  focusedMachineId: number | null = null;

  /** The confirmed totals of the last recorded refill, shown until the next edit; `null` before any confirmation. */
  recordedSummary: string | null = null;

  /**
   * Whether a support session is active (issue #658). While it is, the inventory-writing controls
   * are hidden and {@link save} refuses; see the class doc.
   */
  sessionActive = false;

  /**
   * Set when a save ended without the server's answer - a network failure or a `5xx` - so it is not
   * known whether the refill was recorded. Stays set, and stays on screen, until a later attempt
   * resolves it; while it is set the kept payload and key are the only request that may be sent.
   */
  unresolvedSubmission = false;

  /** The inline wording for {@link unresolvedSubmission}; the same sentence the toast carries. */
  readonly unresolvedMessage = REFILL_UNCERTAIN_MESSAGE;

  /** Per-row refusal messages from the last unattributed save attempt, keyed by product id. */
  rowErrors = new Map<number, readonly string[]>();

  /** Per-cell refusal messages from the last batch save attempt, keyed by {@link refillCellKey}. */
  cellErrors = new Map<string, readonly string[]>();

  /** Refusals about the request as a whole from the last save attempt. */
  requestErrors: string[] = [];

  /** Every machine `LocalMachineService.getAll()` returned, active and inactive alike; see {@link activeMachines}. */
  machines: LocalMachine[] = [];

  /** Set when the local machine list could not be read; the selector is replaced by an explicit message, but saving without a machine still works. */
  machinesLoadFailed = false;

  /** The machines that have a quantity column, in the order the operator selected them; empty is the "No machine" mode. */
  selectedMachineIds: number[] = [];

  /**
   * Set when {@link preselectedMachineId} named a machine that, once the machine read settled, was
   * not one of this business's own active machines - deactivated, removed, or the read itself
   * failed. Reported on screen rather than left to a silent "No machine" default, because the link
   * that opened this entry promised attribution the save would otherwise quietly not deliver.
   */
  machineAttributionFailed = false;

  readonly machineAttributionFailedMessage = MACHINE_ATTRIBUTION_FAILED_MESSAGE;

  readonly machineResolutionPendingMessage = MACHINE_RESOLUTION_PENDING_MESSAGE;

  /** Whether the local machine read has answered, either way; see {@link machineResolutionPending}. */
  private machineReadSettled = false;

  /** The raw text typed into each product's "Taken" box in the "No machine" mode, keyed by product id. */
  private readonly takenById = new Map<number, string>();

  /** The raw text typed into each matrix cell, keyed by {@link refillCellKey}. */
  private readonly cellValues = new Map<string, string>();

  /** The exact payload and key of the last attempted save, kept so a retry resends precisely it. */
  private lastSubmission: ManualPickSubmission | null = null;

  readonly supportSessionMessage = SUPPORT_SESSION_READ_ONLY_MESSAGE;
  readonly catalogueLoadFailedMessage = CATALOGUE_LOAD_FAILED_MESSAGE;
  readonly catalogueRefreshFailedMessage = CATALOGUE_REFRESH_FAILED_MESSAGE;
  readonly machinesLoadFailedMessage = MACHINES_LOAD_FAILED_MESSAGE;
  readonly invalidEntriesPrefix = INVALID_ENTRIES_PREFIX;

  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private readonly pickListService: PickListService,
    private readonly toastService: ToastService,
    supportSession: SupportSessionService,
    private readonly localMachineService: LocalMachineService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnInit(): void {
    this.localMachineService.getAll().subscribe({
      next: (machines) => {
        this.machines = machines;
        this.machinesLoadFailed = false;
        this.machineReadSettled = true;
        this.resolvePreselectedMachine();
      },
      error: () => {
        this.machines = [];
        this.machinesLoadFailed = true;
        this.machineReadSettled = true;
        this.resolvePreselectedMachine();
      }
    });
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  /** The business's active local machines, in the order the service returned them. */
  get activeMachines(): LocalMachine[] {
    return this.machines.filter((machine) => machine.isActive);
  }

  /** Whether the machine selection is offered at all; see the class doc. */
  get machineSelectorShown(): boolean {
    return !this.machinesLoadFailed && this.activeMachines.length > 0;
  }

  /** The selectable machine columns, by the name the business gave each machine. */
  machineOptions(): MultiSelectOption[] {
    return this.activeMachines.map((machine) => ({ id: machine.id, label: machine.name }));
  }

  /** The machines that currently have a quantity column, in column order. */
  get machineColumns(): LocalMachine[] {
    return this.selectedMachineIds
      .map((id) => this.activeMachines.find((machine) => machine.id === id))
      .filter((machine): machine is LocalMachine => machine !== undefined);
  }

  /** Whether the matrix is showing: at least one machine has a column of its own. */
  get matrixMode(): boolean {
    return this.machineColumns.length > 0;
  }

  /**
   * Whether a linked machine's resolution is still outstanding, which only happens while a
   * "Record refill" link named one and the machine read has not answered yet. Save is refused
   * while it is, so a refill is never submitted with an attribution that is neither the linked
   * machine nor a deliberate "No machine", and a kept submission's machine can never change
   * underneath an unresolved retry; see the class doc.
   */
  get machineResolutionPending(): boolean {
    return this.preselectedMachineId !== null && !this.machineReadSettled;
  }

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
      if (this.entryFilter === 'entered' && !this.hasEntry(product.id)) {
        return false;
      }
      if (this.entryFilter === 'shortages' && this.storageExcess(product) === 0) {
        return false;
      }
      return term === '' || product.name.toLowerCase().includes(term);
    });
  }

  /** Whether a product has anything typed against it in the current mode, valid or not. */
  hasEntry(productId: number): boolean {
    if (!this.matrixMode) {
      return holdsQuantity(this.takenById.get(productId));
    }
    return this.machineColumns.some((machine) => holdsQuantity(this.cellValue(productId, machine.id)));
  }

  /** How many products have an entry, whatever the filters hide. */
  enteredProductCount(): number {
    return this.products.filter((product) => this.hasEntry(product.id)).length;
  }

  /**
   * The units this product's entries ask for beyond the storage it is known to hold, summed across
   * **every** selected machine - including entries a filter hides - because they all come off the
   * one shelf. `0` when storage covers them.
   */
  storageExcess(product: Product): number {
    return Math.max(0, this.rowTotal(product.id) - product.quantityInStock);
  }

  /** The products whose entered total exceeds known storage; while any does, a fresh recording is blocked. */
  shortageProducts(): Product[] {
    return this.products.filter((product) => this.storageExcess(product) > 0);
  }

  /** Whether a product's entries are ready to record: something entered, nothing malformed, storage covers it. */
  isReadyToRecord(product: Product): boolean {
    return this.rowTotal(product.id) > 0 && this.storageExcess(product) === 0 && !this.rowHasError(product.id);
  }

  /** How many products are ready to record. */
  readyProductCount(): number {
    return this.products.filter((product) => this.isReadyToRecord(product)).length;
  }

  /** The known suggestion for one product at one local machine, or `null` when nobody suggested one. */
  suggestionFor(productId: number, machineId: number): number | null {
    return this.suggestions.get(refillCellKey(productId, machineId)) ?? null;
  }

  /**
   * Whether an entry is below a known suggestion: information only, never a reason to block. An
   * empty entry and one with no known suggestion are never "below" anything.
   */
  isBelowSuggestion(productId: number, machineId: number): boolean {
    const suggestion = this.suggestionFor(productId, machineId);
    const parsed = parseTakenEntry(this.cellValue(productId, machineId));
    return suggestion !== null && !('error' in parsed) && parsed.quantity > 0 && parsed.quantity < suggestion;
  }

  /** Whether any of a product's entries is below its known suggestion. */
  rowBelowSuggestion(productId: number): boolean {
    return this.matrixMode && this.machineColumns.some((machine) => this.isBelowSuggestion(productId, machine.id));
  }

  /** Clears the search, category and entry filters, revealing every product. */
  showAllProducts(): void {
    this.search = '';
    this.categoryId = '';
    this.entryFilter = 'all';
  }

  /** Clears the search and category filters and shows only the products with an entry, hidden ones included. */
  showEnteredProducts(): void {
    this.search = '';
    this.categoryId = '';
    this.entryFilter = 'entered';
  }

  /** How many entries are malformed on rows the current filters hide. */
  hiddenInvalidEntryCount(): number {
    const visible = this.visibleProductIds();
    return this.products.filter((product) => !visible.has(product.id) && this.rowHasError(product.id)).length;
  }

  /** The machine a phone's cards take entries for: always one of the selected columns. */
  focusedMachine(): LocalMachine | null {
    const columns = this.machineColumns;
    return columns.find((machine) => machine.id === this.focusedMachineId) ?? columns[0] ?? null;
  }

  focusMachine(machineId: number): void {
    this.focusedMachineId = machineId;
  }

  /** Entered units for one machine, across every product. */
  machineUnits(machineId: number): number {
    return this.products.reduce((total, product) => {
      const parsed = parseTakenEntry(this.cellValue(product.id, machineId));
      return 'error' in parsed ? total : total + parsed.quantity;
    }, 0);
  }

  /** How many products have an entry at one machine. */
  machineEntryCount(machineId: number): number {
    return this.products.filter((product) => holdsQuantity(this.cellValue(product.id, machineId))).length;
  }

  /** Whether any quantity is entered at all, in either mode; what the page's departure guard asks about. */
  hasEnteredQuantities(): boolean {
    return this.unattributedEntriesPresent() || [...this.cellValues.values()].some(holdsQuantity);
  }

  /** Whether entries may change right now: not while a save is in flight, and not while an unconfirmed one is outstanding. */
  get entriesLocked(): boolean {
    return this.saving || this.unresolvedSubmission;
  }

  /**
   * Adds `delta` to an entry from the touch controls, never below zero. A malformed entry is left
   * for the operator to fix rather than guessed at.
   */
  stepCell(productId: number, machineId: number | null, delta: number): void {
    const raw = machineId === null ? this.takenValue(productId) : this.cellValue(productId, machineId);
    const parsed = parseTakenEntry(raw);
    if ('error' in parsed) {
      return;
    }
    const next = String(Math.max(0, parsed.quantity + delta));
    if (machineId === null) {
      this.onTakenChange(productId, next);
    } else {
      this.onCellChange(productId, machineId, next);
    }
  }

  /**
   * Drops every entered quantity in the current mode, after asking. Refused while a save is in
   * flight or an unconfirmed one is outstanding, because the entries are then the request itself.
   */
  clearEntries(): void {
    if (this.entriesLocked || !this.hasEnteredQuantities()) {
      return;
    }
    if (!window.confirm(CLEAR_ENTRIES_CONFIRMATION)) {
      return;
    }
    this.takenById.clear();
    this.cellValues.clear();
    this.rowErrors.clear();
    this.cellErrors.clear();
    this.requestErrors = [];
    this.recordedSummary = null;
  }

  /** The primary action's label: what it will do, or why it cannot yet. */
  get primaryActionLabel(): string {
    if (this.saving) {
      return 'Recording…';
    }
    return this.shortageProducts().length > 0 ? RESOLVE_SHORTAGE_LABEL : 'Record refill';
  }

  takenValue(productId: number): string {
    return this.takenById.get(productId) ?? '';
  }

  onTakenChange(productId: number, value: string): void {
    if (this.entriesLocked) {
      return;
    }
    this.takenById.set(productId, value);
    this.rowErrors.delete(productId);
    this.recordedSummary = null;
  }

  /** The one message for this row of the "No machine" column: a malformed entry first, else the server's own last refusal. */
  rowError(productId: number): string | null {
    const parsed = parseTakenEntry(this.takenValue(productId));
    if ('error' in parsed) {
      return parsed.error;
    }
    const messages = this.rowErrors.get(productId);
    return messages && messages.length > 0 ? messages.join(' ') : null;
  }

  cellValue(productId: number, machineId: number): string {
    return this.cellValues.get(refillCellKey(productId, machineId)) ?? '';
  }

  onCellChange(productId: number, machineId: number, value: string): void {
    if (this.entriesLocked) {
      return;
    }
    this.cellValues.set(refillCellKey(productId, machineId), value);
    this.cellErrors.delete(refillCellKey(productId, machineId));
    this.recordedSummary = null;
  }

  /** The one message for this cell: a malformed entry first, else the server's own last per-allocation refusal. */
  cellError(productId: number, machineId: number): string | null {
    const parsed = parseTakenEntry(this.cellValue(productId, machineId));
    if ('error' in parsed) {
      return parsed.error;
    }
    const messages = this.cellErrors.get(refillCellKey(productId, machineId));
    return messages && messages.length > 0 ? messages.join(' ') : null;
  }

  /** The accessible name of one cell, which has to name both things it is about. */
  cellAriaLabel(product: Product, machine: LocalMachine): string {
    return `Quantity taken: ${product.name} at ${machine.name}`;
  }

  /** Every malformed entry, naming the product and - in the matrix - the machine it was typed against. */
  get invalidEntryMessages(): string[] {
    const messages: string[] = [];
    for (const product of this.products) {
      if (this.matrixMode) {
        for (const machine of this.machineColumns) {
          const parsed = parseTakenEntry(this.cellValue(product.id, machine.id));
          if ('error' in parsed) {
            messages.push(`${product.name} at ${machine.name}: ${parsed.error}`);
          }
        }
        continue;
      }
      const parsed = parseTakenEntry(this.takenValue(product.id));
      if ('error' in parsed) {
        messages.push(`${product.name}: ${parsed.error}`);
      }
    }
    return messages;
  }

  /** What this row takes out of storage: in the matrix, its sum across every machine column. */
  rowTotal(productId: number): number {
    if (!this.matrixMode) {
      const parsed = parseTakenEntry(this.takenValue(productId));
      return 'error' in parsed ? 0 : parsed.quantity;
    }
    return this.machineColumns.reduce((total, machine) => {
      const parsed = parseTakenEntry(this.cellValue(productId, machine.id));
      return 'error' in parsed ? total : total + parsed.quantity;
    }, 0);
  }

  /**
   * What the whole pick takes out of storage, over **every** product - including the rows the
   * current search or category filter hides, because a filter changes what is on screen and not what
   * Save sends.
   */
  totalUnitsRequired(): number {
    return this.products.reduce((total, product) => total + this.rowTotal(product.id), 0);
  }

  /** The part of {@link totalUnitsRequired} that comes from rows the current filter hides. */
  hiddenUnitsRequired(): number {
    const visible = this.visibleProductIds();
    return this.products.reduce(
      (total, product) => (visible.has(product.id) ? total : total + this.rowTotal(product.id)),
      0
    );
  }

  /** How many products with a typed quantity the current filter hides. */
  hiddenProductsWithEntries(): number {
    const visibleIds = this.visibleProductIds();
    return this.products.filter((product) => !visibleIds.has(product.id) && this.hasEntry(product.id)).length;
  }

  /**
   * Applies a new machine selection, asking first whenever it would drop a typed quantity, and
   * changing nothing at all when that is refused. Blocked outright while a save is in flight: the
   * request being answered is the request that was built.
   */
  onSelectedMachineIdsChange(machineIds: number[]): void {
    if (this.entriesLocked) {
      return;
    }

    const next = machineIds.filter((id) => this.activeMachines.some((machine) => machine.id === id));
    const previous = this.selectedMachineIds;
    const removed = previous.filter((id) => !next.includes(id));

    if (previous.length === 0 && next.length > 0 && this.unattributedEntriesPresent()) {
      if (!window.confirm(DISCARD_UNATTRIBUTED_ENTRIES_CONFIRMATION)) {
        return;
      }
      this.discardUnattributedEntries();
    }

    const removedWithEntries = removed.filter((id) => this.machineEntriesPresent(id));
    if (removedWithEntries.length > 0) {
      // Leaving the matrix altogether is one decision about every column, not one question per
      // machine; removing some of them names exactly the ones whose quantities are about to go.
      const question =
        next.length === 0
          ? DISCARD_MACHINE_COLUMNS_CONFIRMATION
          : discardMachineColumnConfirmation(...removedWithEntries.map((id) => this.machineName(id)));
      if (!window.confirm(question)) {
        return;
      }
    }

    this.selectedMachineIds = next;
    for (const id of removed) {
      this.discardMachineEntries(id);
    }
  }

  /** Removes one machine's column, asking first when it holds quantities. */
  removeMachineColumn(machineId: number): void {
    this.onSelectedMachineIdsChange(this.selectedMachineIds.filter((id) => id !== machineId));
  }

  get canSave(): boolean {
    if (!this.readyToSend) {
      return false;
    }
    if (this.unresolvedSubmission) {
      return this.lastSubmission !== null;
    }
    // A fresh recording is blocked while entered totals exceed known storage. The server stays
    // authoritative - it weighs the batch against storage again inside its own transaction - but a
    // refill the screen already knows cannot fit is not offered. An unconfirmed retry above is
    // never blocked by this, because it must resend exactly what it sent.
    if (this.shortageProducts().length > 0) {
      return false;
    }
    const payload = this.buildPayload();
    return payload !== null && !isEmptyPayload(payload);
  }

  /** Whether the explicit Retry of an uncertain attempt is available. */
  get canRetry(): boolean {
    return this.readyToSend && this.unresolvedSubmission && this.lastSubmission !== null;
  }

  /**
   * Sends the pick as one request. While an outcome is uncertain this *is* {@link retry}: the kept
   * payload and key are the only request that may be sent until that attempt is resolved, so no edit,
   * column removal or reordering can mint a new key and turn one physical pick into two deductions.
   */
  save(): void {
    if (this.unresolvedSubmission) {
      this.retry();
      return;
    }
    // Checked here as well as hidden in the template, so the write is actually unavailable during a
    // support session rather than merely invisible - the endpoint refuses it either way. The
    // pending-resolution check is here for the same reason, and because the machine the link named
    // must not be able to arrive after this request was built: see machineResolutionPending.
    if (!this.readyToSend) {
      return;
    }

    const payload = this.buildPayload();
    if (payload === null || isEmptyPayload(payload) || this.shortageProducts().length > 0) {
      return;
    }

    const kept = this.lastSubmission;
    const reuse = kept !== null && payloadsEqual(kept.payload, payload);
    this.send(reuse ? kept : { key: generateIdempotencyKey(), payload });
  }

  /** Resends the uncertain attempt exactly: the same key, the same payload, the same endpoint. */
  retry(): void {
    if (!this.canRetry) {
      return;
    }
    this.send(this.lastSubmission!);
  }

  /** Whether a request may leave at all right now, whatever is typed. */
  private get readyToSend(): boolean {
    return !this.saving && !this.sessionActive && !this.machineResolutionPending;
  }

  /**
   * Accepts {@link preselectedMachineId} as the real selection only if it names one of the machines
   * the read just answered with and that machine is active; otherwise raises
   * {@link machineAttributionFailed} instead of leaving the failure unreported. Does nothing when no
   * machine was linked at all.
   */
  private resolvePreselectedMachine(): void {
    if (this.preselectedMachineId === null) {
      return;
    }
    const match = this.activeMachines.find((machine) => machine.id === this.preselectedMachineId);
    if (!match) {
      this.machineAttributionFailed = true;
      return;
    }

    this.selectedMachineIds = [match.id];
    // A quantity typed while this machine was being confirmed belongs to it - that is what the link
    // and the pending notice both promised - so it moves into its column rather than being dropped
    // or left as a pick against no machine. This is the one place an entry changes column, and it is
    // one machine, never a copy into several.
    for (const [productId, raw] of this.takenById) {
      if (holdsQuantity(raw)) {
        this.cellValues.set(refillCellKey(productId, match.id), raw);
      }
    }
    this.discardUnattributedEntries();
  }

  /** Whether any of a product's entries in the current mode is malformed. */
  private rowHasError(productId: number): boolean {
    if (!this.matrixMode) {
      return this.rowError(productId) !== null;
    }
    return this.machineColumns.some((machine) => this.cellError(productId, machine.id) !== null);
  }

  /** Whether the "No machine" column holds anything the operator would mind losing. */
  private unattributedEntriesPresent(): boolean {
    return [...this.takenById.values()].some(holdsQuantity);
  }

  /** Whether one machine's column holds anything the operator would mind losing. */
  private machineEntriesPresent(machineId: number): boolean {
    return this.products.some((product) => holdsQuantity(this.cellValue(product.id, machineId)));
  }

  private discardUnattributedEntries(): void {
    this.takenById.clear();
    this.rowErrors.clear();
  }

  private discardMachineEntries(machineId: number): void {
    for (const product of this.products) {
      this.cellValues.delete(refillCellKey(product.id, machineId));
      this.cellErrors.delete(refillCellKey(product.id, machineId));
    }
  }

  private machineName(machineId: number): string {
    return this.activeMachines.find((machine) => machine.id === machineId)?.name ?? `Machine #${machineId}`;
  }

  private visibleProductIds(): Set<number> {
    return new Set(this.visibleProducts().map((product) => product.id));
  }

  /**
   * Exactly what Save would send now, or `null` while any entry of the current mode is malformed -
   * including one on a row the filter hides, because a hidden entry is still part of the pick.
   */
  private buildPayload(): ManualPickPayload | null {
    if (!this.matrixMode) {
      const rows = this.buildUnattributedRows();
      return rows === null ? null : { kind: 'unattributed', rows };
    }

    const allocations = this.buildBatchAllocations();
    return allocations === null ? null : { kind: 'batch', allocations };
  }

  private buildUnattributedRows(): PickListRefillRowRequest[] | null {
    const rows: PickListRefillRowRequest[] = [];
    for (const product of this.products) {
      const parsed = parseTakenEntry(this.takenValue(product.id));
      if ('error' in parsed) {
        return null;
      }
      if (parsed.quantity > 0) {
        rows.push({ productId: product.id, quantityTaken: parsed.quantity });
      }
    }
    return rows;
  }

  private buildBatchAllocations(): MultiMachineRefillAllocationRequest[] | null {
    const allocations: MultiMachineRefillAllocationRequest[] = [];
    for (const product of this.products) {
      for (const machine of this.machineColumns) {
        const parsed = parseTakenEntry(this.cellValue(product.id, machine.id));
        if ('error' in parsed) {
          return null;
        }
        if (parsed.quantity > 0) {
          allocations.push({ machineId: machine.id, productId: product.id, quantityTaken: parsed.quantity });
        }
      }
    }
    return allocations;
  }

  private send(submission: ManualPickSubmission): void {
    this.lastSubmission = submission;
    this.saving = true;
    this.unresolvedSubmission = false;
    this.requestErrors = [];
    this.rowErrors.clear();
    this.cellErrors.clear();
    this.saveInFlightChange.emit(true);

    this.sendRequest(submission).subscribe({
      next: () => {
        this.saving = false;
        this.lastSubmission = null;
        this.discardRecordedEntries(submission.payload);
        this.saveInFlightChange.emit(false);
        this.recordedSummary = recordedSummary(submission.payload);
        this.toastService.success(recordedMessage(submission.payload));
        this.refillRecorded.emit();
      },
      error: (error: unknown) => {
        this.saving = false;
        this.saveInFlightChange.emit(false);
        this.handleSaveError(error, submission.payload);
      }
    });
  }

  /** The one place a payload becomes a call: a batch goes to issue #897's endpoint, an unattributed pick to #743's. */
  private sendRequest(submission: ManualPickSubmission): Observable<unknown> {
    if (submission.payload.kind === 'batch') {
      const request: MultiMachinePickListRefillRequest = {
        idempotencyKey: submission.key,
        allocations: submission.payload.allocations
      };
      return this.pickListService.recordMultiMachineRefill(request);
    }

    const request: PickListRefillRequest = { idempotencyKey: submission.key, rows: submission.payload.rows };
    return this.pickListService.recordRefill(request);
  }

  /** Clears only the mode that was actually recorded, so the other mode's draft is never touched by it. */
  private discardRecordedEntries(payload: ManualPickPayload): void {
    if (payload.kind === 'batch') {
      this.cellValues.clear();
      return;
    }
    this.takenById.clear();
  }

  private handleSaveError(error: unknown, payload: ManualPickPayload): void {
    // Nothing was recorded by a refusal of either shape: the request and state rules both refuse
    // before the idempotency key is ever claimed, so the same key is safe to resend once the
    // operator fixes what the refusal named - `lastSubmission` is left exactly as it is.
    if (payload.kind === 'batch') {
      const rejection = parseMultiMachineRefillRejection(error, payload.allocations);
      if (rejection !== null) {
        this.cellErrors = new Map(rejection.cellErrors);
        this.requestErrors = [...rejection.requestErrors];
        return;
      }
    } else {
      const rejection = parsePickListRefillRejection(error, payload.rows);
      if (rejection !== null) {
        this.rowErrors = rejection.rowErrors;
        this.requestErrors = [...rejection.requestErrors];
        return;
      }
    }

    if (error instanceof HttpErrorResponse && error.status === 403) {
      // A definite refusal (a support session's read-only rule, or a capability this member lacks):
      // the request was refused before anything was recorded, so there is nothing to retry and no
      // uncertain write to warn about. The key is dropped so a later, permitted save mints its own.
      this.lastSubmission = null;
      this.toastService.error(this.problemDetail(error, REFILL_FORBIDDEN_MESSAGE));
      return;
    }

    if (error instanceof HttpErrorResponse && error.status === 409) {
      // The key is spent on a different request; reusing it would only be refused again.
      this.lastSubmission = null;
      this.toastService.error(this.problemDetail(error, REFILL_CONFLICT_MESSAGE));
      return;
    }

    // An uncertain outcome - a network failure, a 5xx - so the exact payload and key stay ready and
    // become the only request that may be sent: Retry resends precisely this one. The notice stays
    // on screen until that retry resolves it, because a toast the operator missed is not a reason to
    // let an unconfirmed pick look finished.
    this.unresolvedSubmission = true;
    this.toastService.error(REFILL_UNCERTAIN_MESSAGE);
  }

  /** The response's own ProblemDetails detail sentence, falling back to the given generic one. */
  private problemDetail(error: HttpErrorResponse, fallback: string): string {
    const body = error.error as { detail?: unknown } | null;
    const detail = body && typeof body === 'object' ? body.detail : null;
    return typeof detail === 'string' && detail.trim().length > 0 ? detail : fallback;
  }
}

/** Whether a payload would record nothing at all, which is not a pick to send. */
function isEmptyPayload(payload: ManualPickPayload): boolean {
  return payload.kind === 'batch' ? payload.allocations.length === 0 : payload.rows.length === 0;
}

/**
 * Whether two payloads are the same submission. A batch compares as a set of machine/product/quantity
 * allocations, so merely reordering the machine columns is still the same pick under the same key.
 */
function payloadsEqual(a: ManualPickPayload, b: ManualPickPayload): boolean {
  if (a.kind === 'batch' && b.kind === 'batch') {
    return multiMachineAllocationsEqual(a.allocations, b.allocations);
  }
  if (a.kind === 'unattributed' && b.kind === 'unattributed') {
    return pickListRefillRowsEqual(a.rows, b.rows);
  }
  return false;
}

function recordedSummary(payload: ManualPickPayload): string {
  if (payload.kind !== 'batch') {
    return refillRecordedSummary(payload.rows.reduce((total, row) => total + row.quantityTaken, 0), 0);
  }
  const machineCount = new Set(payload.allocations.map((allocation) => allocation.machineId)).size;
  return refillRecordedSummary(payload.allocations.reduce((total, allocation) => total + allocation.quantityTaken, 0), machineCount);
}

function recordedMessage(payload: ManualPickPayload): string {
  if (payload.kind !== 'batch') {
    return REFILL_RECORDED_MESSAGE;
  }
  const machineCount = new Set(payload.allocations.map((allocation) => allocation.machineId)).size;
  return multiMachineRefillRecordedMessage(payload.allocations.length, machineCount);
}
