import { CommonModule } from '@angular/common';
import { Component, ElementRef, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges, ViewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BusinessDateTimePipe } from '../../../formatting/business-date-time.pipe';
import { Category, Machine, PickListMachineQuantity, PickListProduct, Product, SiteListItem } from '../../../models/models';
import { PickListService } from '../../../services/pick-list.service';
import { IconComponent } from '../../shared/icon.component';
import { MultiSelectDropdownComponent, MultiSelectOption } from '../../shared/multi-select-dropdown.component';
import { SortDirection, compareSortValues, nextSortState, sortAriaState, sortColumnLabel } from '../../shared/sortable-column';
import { MAXIMUM_PICK_QUANTITY, parsePlannedQuantity } from '../pick-list-quantity';

export type PlanPicksSortColumn = 'name' | 'mdbCode';

/** The one sentence that says what Plan picks does, and does not do, to inventory (issue #996). */
export const PLANNING_ONLY_MESSAGE = 'Planning only. Editing quantities and checking picks does not change inventory.';

/** What the operator is asked before a refresh rebuilds the plan and drops the work they did on it. */
export const DISCARD_PLANNING_WORK_ON_REFRESH =
  'Refresh builds a new plan from the latest snapshot. The quantities you edited and the picks you checked will be discarded. Continue?';

/** What the operator is asked before Reset clears the selection along with that work. */
export const DISCARD_PLANNING_WORK_ON_RESET =
  'Reset clears your selection, the quantities you edited and the picks you checked. Continue?';

/**
 * What the operator is asked before applying a selection that leaves out the row they are still
 * typing a quantity into. The machine-removal question below covers an editor whose own column is
 * going; this one covers the case it cannot - the column stays and the Products filter drops the row.
 */
export const DISCARD_OPEN_EDIT_ON_APPLY =
  'The quantity you are editing has not been confirmed. Applying this selection discards it. Continue?';

/** What the operator is asked before machine columns that hold edited or checked picks are removed. */
export function discardPlanningWorkOnMachineRemoval(...machineNames: string[]): string {
  const columns = machineNames.length === 1 ? 'column' : 'columns';
  return `Remove the ${machineNames.join(', ')} ${columns}? The quantities you edited and the picks you checked there will be discarded.`;
}

/**
 * What a failed snapshot read says. It deliberately does not ask for an Apply: re-applying an
 * unchanged machine selection fetches nothing, so telling the operator to press it would be an
 * instruction that cannot work. Refresh is the retry.
 */
export const PLAN_LOAD_FAILED_MESSAGE = 'The plan could not be loaded. Use Refresh to try again.';

/** One site's machine columns, kept adjacent so one site header can span them (issue #996). */
export interface PlanPicksMachineGroup {
  readonly key: string;
  /** The site these columns stand at, or `null` for the machines at no known site. */
  readonly siteName: string | null;
  readonly machineIds: readonly number[];
}

/**
 * **Plan picks** (issue #996): the product-first planning matrix over the read-only
 * `GET /api/pick-list` projection, with the planning overrides, completion marks and shortage
 * visibility the approved Restockroo design adds to it.
 *
 * It is a dedicated feature component rather than more markup on `PickListComponent`
 * (AGENTS.md § Frontend rules, docs/architecture.md § Page composition boundary): it owns a
 * distinct workflow with substantial UI of its own plus its own state, actions and load lifecycle -
 * the staged/applied selections, the snapshot it fetched, its planning overrides and completion
 * marks, its own view filters and sort, and the mobile machine focus. The page keeps owning the
 * reads every view shares (the machine fleet, the site list, the active catalogue) and hands them
 * in; it also owns the mode choice and the navigation guard, which this component only reports into
 * through {@link planningWorkChange}.
 *
 * **Nothing here writes anything.** Every quantity edit and every checked pick is a local override
 * in this component instance: no mutation endpoint is called, no target is changed, and no stock
 * moves. {@link PLANNING_ONLY_MESSAGE} says exactly that on screen, and there is deliberately no
 * transfer from here into Record refill - a plan is not a record of what was taken.
 *
 * **An override preserves the suggestion it replaced.** `pickListProducts` stays exactly as the
 * backend sent it, so a cell's suggested quantity is always readable; {@link overrides} holds only
 * what the operator committed instead, and the cell shows "Suggested: N" whenever the two differ.
 * Committing a changed quantity clears that cell's completion mark, because a pick of a different
 * size has not been done yet. A committed `0` is a deliberate skip: it leaves the cell out of the
 * actionable count and the progress figure, and stays editable and resettable.
 *
 * **A quantity still being typed is planning work too.** An open editor counts as soon as its text
 * would commit something other than the quantity it was opened on ({@link hasDirtyEditor}), so
 * {@link hasPlanningWork} and {@link planningWorkChange} cover it and Refresh, Reset, a removed
 * column, an applied selection that drops its row, and leaving the page all ask before it goes. An
 * editor merely opened, one typed back to what it found, and an explicit Cancel are not work and
 * warn about nothing.
 *
 * **Totals, shortages and progress are the applied run's**, over every applied machine and every
 * product in the snapshot, with the committed overrides in place - never only the rows a filter
 * leaves visible. The shortage is the projection's own rule
 * (`max(0, total to pick - storage quantity)`, `GetPickList`), recomputed over the committed
 * quantities because the server cannot know them; with no override at all it is the figure the
 * server already sent.
 *
 * **A stale response can neither replace a newer selection nor resurrect a cleared plan.** Every
 * change to the applied machine selection - Apply, a removed machine, Reset - bumps
 * {@link appliedToken}, and a response is accepted only while the token it was requested under is
 * still the current one. Clearing the selection bumps it too, so a request that was already in
 * flight lands on nothing.
 *
 * **Refresh is separate from Apply, and it is the retry.** Apply applies the staged selections and
 * only fetches when the machine selection actually changed; Refresh re-reads the *currently applied*
 * selection whether or not it changed, which is what makes a failed snapshot retryable without
 * pretending a selection change is needed.
 */
@Component({
  selector: 'app-plan-picks',
  standalone: true,
  imports: [CommonModule, FormsModule, MultiSelectDropdownComponent, BusinessDateTimePipe, IconComponent],
  templateUrl: './plan-picks.component.html'
})
export class PlanPicksComponent implements OnChanges, OnDestroy {
  /** The provider machine fleet, as `GET api/machines` returned it; the matrix' columns come from it. */
  @Input() machines: Machine[] = [];

  /**
   * The business's sites, as `GET api/sites/list` returned them, or empty when that read found none
   * or failed. Only the names and the grouping come from it: a machine whose site is unknown keeps
   * its own column under no site header, and nothing here invents a site for it.
   */
  @Input() sites: SiteListItem[] = [];

  /** The active catalogue, for the Products filter options and the Category filter. */
  @Input() products: Product[] = [];

  /** What to say instead of a selection and a matrix when the machine fleet could not be read. */
  @Input() machinesError = '';

  /**
   * Emitted whenever this view starts or stops holding planning work - a committed override, a
   * checked pick, or a quantity being typed that has not been committed yet - so the page's
   * navigation guard can warn before it is lost.
   */
  @Output() readonly planningWorkChange = new EventEmitter<boolean>();

  /**
   * Emitted with the error of a snapshot read that failed, so the page can decide whether it says
   * anything about the provider. This view reports the failure and offers Refresh; whether a Nayax
   * permission refusal has just been proved - and therefore whether planning is available at all -
   * is the page's question, and it owns the provider availability that answers it.
   */
  @Output() readonly loadFailure = new EventEmitter<unknown>();

  readonly planningOnlyMessage = PLANNING_ONLY_MESSAGE;
  readonly loadFailedMessage = PLAN_LOAD_FAILED_MESSAGE;
  readonly maximumQuantity = MAXIMUM_PICK_QUANTITY;

  /** Staged filter choices from the dropdowns, not yet applied to the matrix. */
  stagedMachineIds: number[] = [];
  stagedProductIds: number[] = [];

  /** The last-applied filter choices: what the matrix columns/rows and {@link pickListProducts} reflect. */
  appliedMachineIds: number[] = [];
  appliedProductIds: number[] = [];

  /** The last-fetched read-only projection for {@link appliedMachineIds}; never edited in place. */
  pickListProducts: PickListProduct[] = [];

  /** View filters, applied immediately: they hide rows and change no override, mark or total. */
  search = '';
  categoryId: number | '' = '';
  onlyRemaining = false;
  onlyShortages = false;

  /** Client-side only: until a header is clicked, rows stay in the backend's default order. */
  sortColumn: PlanPicksSortColumn | null = null;
  sortDirection: SortDirection = 'asc';

  lastRefreshedAt: Date | null = null;

  /** Whether a snapshot read is outstanding, and whether the last accepted one failed. */
  loading = false;
  loadFailed = false;

  /** The machine whose picks the narrow layout's product cards show; `null` before one is applied. */
  focusMachineId: number | null = null;

  /** The cell being edited, as a `productId:machineId` key, or `null` while none is. */
  editingKey: string | null = null;

  /** The text in the open editor, and the message refusing it; `''` while it is acceptable. */
  editValue = '';
  editError = '';

  /**
   * The quantity the open editor was opened on, as the text it was opened with, so an editor that
   * has actually been typed into can be told from one that is merely open. `''` while none is open.
   */
  private editBaseline = '';

  /** The committed planning overrides, keyed by `productId:machineId`. Never sent anywhere. */
  private readonly overrides = new Map<string, number>();

  /** The cells the operator has checked off, keyed the same way. */
  private readonly completed = new Set<string>();

  /**
   * Bumped by every change to the applied machine selection. A response is accepted only while the
   * token it was requested under is still current, so a slow response can neither overwrite a newer
   * selection's data nor bring back a plan that has been cleared.
   */
  private appliedToken = 0;

  /** The last {@link planningWorkChange} value emitted, so the page is told only about real changes. */
  private planningWorkReported = false;

  /** Whether the Products filter has been seeded from the catalogue; see {@link ngOnChanges}. */
  private productSelectionSeeded = false;

  constructor(private readonly pickListService: PickListService) {}

  /**
   * Focuses and selects the open editor's value as soon as it exists, so editing starts on a
   * selected number the operator can type straight over.
   *
   * Both layouts are in the document at once - only CSS hides one - so there are two editors to
   * consider and {@link focusEditor} gives the first one that actually takes focus. On a wide
   * viewport that is the matrix editor; on a phone the matrix is `display: none`, where `focus()`
   * does nothing, so the card editor gets it instead.
   */
  @ViewChild('editInput')
  set editInput(input: ElementRef<HTMLInputElement> | undefined) {
    this.focusEditor(input);
  }

  @ViewChild('cardEditInput')
  set cardEditInput(input: ElementRef<HTMLInputElement> | undefined) {
    this.focusEditor(input);
  }

  /** Whether the machine selection and the matrix are offered at all. */
  get machineSelectionShown(): boolean {
    return this.machinesError === '';
  }

  /**
   * Whether this view holds work an operator would mind losing: a committed override, a checked
   * pick, or a quantity being typed that has not been committed yet. An open editor counts only
   * once it has actually been changed - see {@link hasDirtyEditor} - so merely opening one, or
   * cancelling it, never produces a warning about nothing.
   */
  get hasPlanningWork(): boolean {
    return this.overrides.size > 0 || this.completed.size > 0 || this.hasDirtyEditor;
  }

  /**
   * Whether the open editor holds a draft that would be lost. The test is what it would commit
   * against what it opened with: "4" left alone, "04" and " 4 " all commit the quantity already
   * planned and are not work, while "7", a cleared box and "abc" are each something typed that a
   * discard would throw away - a refused value included, because it is still what the operator has
   * in front of them.
   */
  get hasDirtyEditor(): boolean {
    return this.dirtyEditorKey !== null;
  }

  /** The open editor's cell key while it holds such a draft, else `null`. */
  private get dirtyEditorKey(): string | null {
    if (this.editingKey === null || this.editValue === this.editBaseline) {
      return null;
    }
    const parsed = parsePlannedQuantity(this.editValue);
    return 'error' in parsed || String(parsed.quantity) !== this.editBaseline ? this.editingKey : null;
  }

  // ----- selection -------------------------------------------------------------------------

  /**
   * The machine options, grouped by site where the site list and the machines' own `siteId` know
   * one. A machine at no known site carries no group, so it stays selectable without an invented
   * site.
   */
  machineOptions(): MultiSelectOption[] {
    return this.orderedMachines().map((machine) => ({
      id: machine.machineID,
      label: this.machineLabel(machine.machineID),
      group: this.siteNameOf(machine) ?? undefined
    }));
  }

  productOptions(): MultiSelectOption[] {
    return this.products.map((product) => ({ id: product.id, label: product.name }));
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

  onStagedMachineIdsChange(ids: number[]): void {
    this.stagedMachineIds = ids;
  }

  onStagedProductIdsChange(ids: number[]): void {
    this.stagedProductIds = ids;
  }

  machineLabel(machineId: number): string {
    const machine = this.machines.find((m) => m.machineID === machineId);
    if (!machine) {
      return `Machine #${machineId}`;
    }
    return machine.machineName || machine.machineNumber || `Machine #${machineId}`;
  }

  /**
   * Applies the staged Products/Machines selections together; only a changed machine selection
   * refetches. Removing a machine that holds planning work asks first, as does leaving out the row
   * an uncommitted quantity is being typed into, and a refusal changes nothing at all - neither the
   * selection, nor one override, nor the editor.
   */
  applyFilters(): void {
    const removed = this.appliedMachineIds.filter((id) => !this.stagedMachineIds.includes(id));
    if (!this.confirmMachineRemoval(removed)) {
      return;
    }
    if (!this.confirmOpenEditDiscard(this.stagedMachineIds, this.stagedProductIds)) {
      return;
    }

    const machineSelectionChanged = !sameIds(this.stagedMachineIds, this.appliedMachineIds);
    this.appliedProductIds = [...this.stagedProductIds];
    this.appliedMachineIds = [...this.stagedMachineIds];
    this.closeEditorUnlessStillPlanned();
    removed.forEach((id) => this.discardMachineWork(id));

    if (machineSelectionChanged) {
      this.fetchOrClear();
    } else {
      this.alignFocusMachine();
    }
  }

  /** Removes one machine column from both selections, asking first when it holds planning work. */
  removeMachine(machineId: number): void {
    if (!this.confirmMachineRemoval([machineId])) {
      return;
    }
    this.appliedMachineIds = this.appliedMachineIds.filter((id) => id !== machineId);
    this.stagedMachineIds = this.stagedMachineIds.filter((id) => id !== machineId);
    this.closeEditorUnlessStillPlanned();
    this.discardMachineWork(machineId);
    this.fetchOrClear();
  }

  /**
   * Re-reads the applied machine selection, whether or not it changed - the retry a failed snapshot
   * needs, and the only way to take a fresh snapshot without changing the selection. Planning work
   * is about the snapshot it was done against, so a confirmed refresh discards it and the new plan
   * is calculated from the new snapshot.
   */
  refresh(): void {
    if (this.appliedMachineIds.length === 0) {
      return;
    }
    if (this.hasPlanningWork && !window.confirm(DISCARD_PLANNING_WORK_ON_REFRESH)) {
      return;
    }
    this.discardAllPlanningWork();
    this.fetch([...this.appliedMachineIds]);
  }

  resetAll(): void {
    if (this.hasPlanningWork && !window.confirm(DISCARD_PLANNING_WORK_ON_RESET)) {
      return;
    }
    const allProductIds = this.products.map((p) => p.id);
    this.stagedProductIds = allProductIds;
    this.appliedProductIds = allProductIds;
    this.stagedMachineIds = [];
    this.appliedMachineIds = [];
    this.pickListProducts = [];
    this.search = '';
    this.categoryId = '';
    this.onlyRemaining = false;
    this.onlyShortages = false;
    this.sortColumn = null;
    this.sortDirection = 'asc';
    this.lastRefreshedAt = null;
    this.loading = false;
    this.loadFailed = false;
    this.focusMachineId = null;
    this.discardAllPlanningWork();
    this.appliedToken++;
  }

  /**
   * Seeds the Products filter with every product the first time the page's catalogue read answers
   * with one, so the matrix starts unfiltered and the trigger reads "All products". A later read -
   * the refresh after a recorded refill, or a retry - only replaces the options, never the
   * operator's own selection.
   */
  ngOnChanges(changes: SimpleChanges): void {
    if (changes['products'] && !this.productSelectionSeeded && this.products.length > 0) {
      this.productSelectionSeeded = true;
      this.stagedProductIds = this.products.map((product) => product.id);
      this.appliedProductIds = [...this.stagedProductIds];
    }
  }

  /**
   * Planning work lives in this instance and nowhere else, so a destroyed view holds none: the page
   * is told so, rather than left warning about work that no longer exists anywhere.
   */
  ngOnDestroy(): void {
    if (this.planningWorkReported) {
      this.planningWorkReported = false;
      this.planningWorkChange.emit(false);
    }
  }

  // ----- columns ---------------------------------------------------------------------------

  /**
   * The applied machine columns, grouped so every column of one site is adjacent and one site
   * header can span them. Groups follow the site list's own order; the machines at no known site
   * come last, in fleet order, under no site header at all.
   */
  get machineGroups(): readonly PlanPicksMachineGroup[] {
    const applied = new Set(this.appliedMachineIds);
    const groups: PlanPicksMachineGroup[] = [];
    const placed = new Set<number>();
    for (const site of this.sites) {
      const machineIds = this.machines
        .filter((machine) => applied.has(machine.machineID) && machine.siteId != null && machine.siteId === site.siteId)
        .map((machine) => machine.machineID);
      machineIds.forEach((id) => placed.add(id));
      if (machineIds.length > 0) {
        groups.push({ key: `site-${site.siteId}`, siteName: site.name, machineIds });
      }
    }
    const remaining = this.appliedMachineIds.filter((id) => !placed.has(id));
    if (remaining.length > 0) {
      groups.push({ key: 'no-site', siteName: null, machineIds: remaining });
    }
    return groups;
  }

  /** The applied machine ids in column order: site groups first, then the machines at no known site. */
  get machineColumnIds(): readonly number[] {
    return this.machineGroups.flatMap((group) => [...group.machineIds]);
  }

  /** Whether any applied column has a site, which is the only case a site header row is drawn for. */
  get siteHeaderShown(): boolean {
    return this.machineGroups.some((group) => group.siteName !== null);
  }

  /** Whether the horizontal-scroll hint is worth showing: more columns than comfortably fit. */
  get horizontalScrollHintShown(): boolean {
    return this.machineColumnIds.length > 2;
  }

  // ----- rows ------------------------------------------------------------------------------

  /** The rows on screen: the applied product selection, the view filters, then the active sort. */
  visibleProducts(): PickListProduct[] {
    const term = this.search.trim().toLowerCase();
    const filtered = this.pickListProducts.filter((product) => {
      if (!this.appliedProductIds.includes(product.productId)) {
        return false;
      }
      if (term !== '' && !product.productName.toLowerCase().includes(term)) {
        return false;
      }
      if (this.categoryId !== '' && this.categoryOf(product.productId) !== this.categoryId) {
        return false;
      }
      if (this.onlyShortages && this.shortageFor(product) <= 0) {
        return false;
      }
      if (this.onlyRemaining && this.remainingPicksFor(product) === 0) {
        return false;
      }
      return true;
    });
    return this.sortColumn === null ? filtered : this.sortProducts(filtered);
  }

  sortBy(column: PlanPicksSortColumn): void {
    const next = nextSortState(this.sortColumn, this.sortDirection, column);
    this.sortColumn = next.column;
    this.sortDirection = next.direction;
  }

  ariaSort(column: PlanPicksSortColumn): 'ascending' | 'descending' | 'none' {
    return sortAriaState(this.sortColumn, this.sortDirection, column);
  }

  sortLabel(label: string, column: PlanPicksSortColumn): string {
    return sortColumnLabel(label, this.sortColumn, this.sortDirection, column);
  }

  cellFor(product: PickListProduct, machineId: number): PickListMachineQuantity | undefined {
    return product.machineQuantities.find((m) => m.machineId === machineId);
  }

  /** One product's cell for the machine the narrow layout is focused on, if it has one. */
  focusedCellFor(product: PickListProduct): PickListMachineQuantity | undefined {
    const machineId = this.focusedMachineId;
    return machineId === null ? undefined : this.cellFor(product, machineId);
  }

  // ----- planning overrides ----------------------------------------------------------------

  /** What this cell is planned to pick now: the committed override, else the backend's suggestion. */
  pickQuantity(productId: number, cell: PickListMachineQuantity): number {
    return this.overrides.get(cellKey(productId, cell.machineId)) ?? cell.quantityToPick;
  }

  /** Whether the operator has committed a quantity of their own for this cell. */
  isOverridden(productId: number, machineId: number): boolean {
    return this.overrides.has(cellKey(productId, machineId));
  }

  /** Whether the committed quantity differs from the suggestion, which is when it is worth stating. */
  suggestionShown(productId: number, cell: PickListMachineQuantity): boolean {
    return this.pickQuantity(productId, cell) !== cell.quantityToPick;
  }

  /**
   * Whether this cell is a pick to do: a positive planned quantity. A cell the backend suggests
   * nothing for is not editable - there is no pick there to plan - and a committed zero is a
   * deliberate skip, which stays editable through {@link isEditable} without counting as a pick.
   */
  isActionable(productId: number, cell: PickListMachineQuantity): boolean {
    return this.pickQuantity(productId, cell) > 0;
  }

  /** Whether this cell may be edited: a suggested pick, or one the operator has already overridden. */
  isEditable(productId: number, cell: PickListMachineQuantity): boolean {
    return cell.quantityToPick > 0 || this.isOverridden(productId, cell.machineId);
  }

  isCompleted(productId: number, machineId: number): boolean {
    return this.completed.has(cellKey(productId, machineId));
  }

  /** Checks or unchecks one pick. Only an actionable cell can be completed. */
  toggleCompleted(productId: number, cell: PickListMachineQuantity): void {
    if (!this.isActionable(productId, cell)) {
      return;
    }
    const key = cellKey(productId, cell.machineId);
    if (this.completed.has(key)) {
      this.completed.delete(key);
    } else {
      this.completed.add(key);
    }
    this.notifyPlanningWork();
  }

  editKeyFor(productId: number, machineId: number): string {
    return cellKey(productId, machineId);
  }

  isEditing(productId: number, machineId: number): boolean {
    return this.editingKey === cellKey(productId, machineId);
  }

  /**
   * Opens the editor on this cell's current planned quantity. Opening one is not work in itself -
   * the baseline it records is what makes a later keystroke one - but opening a *second* editor
   * drops whatever was being typed into the first, so the page is told either way.
   */
  startEdit(productId: number, cell: PickListMachineQuantity): void {
    if (!this.isEditable(productId, cell)) {
      return;
    }
    this.editingKey = cellKey(productId, cell.machineId);
    this.editValue = String(this.pickQuantity(productId, cell));
    this.editBaseline = this.editValue;
    this.editError = '';
    this.notifyPlanningWork();
  }

  /**
   * Commits the open editor. A malformed, fractional, negative or overflowing value is refused
   * inline and the editor stays open with what was typed, so nothing is silently rounded or
   * dropped. A committed change clears that cell's completion mark: a pick of a different size has
   * not been done yet. Committing the suggestion itself removes the override rather than storing a
   * value equal to it.
   */
  commitEdit(productId: number, cell: PickListMachineQuantity): void {
    if (!this.isEditing(productId, cell.machineId)) {
      return;
    }
    const parsed = parsePlannedQuantity(this.editValue);
    if ('error' in parsed) {
      this.editError = parsed.error;
      this.notifyPlanningWork();
      return;
    }

    const key = cellKey(productId, cell.machineId);
    const previous = this.pickQuantity(productId, cell);
    if (parsed.quantity === cell.quantityToPick) {
      this.overrides.delete(key);
    } else {
      this.overrides.set(key, parsed.quantity);
    }
    if (parsed.quantity !== previous) {
      this.completed.delete(key);
    }
    this.closeEditor();
    this.alignFocusMachine();
    this.notifyPlanningWork();
  }

  /**
   * Closes the editor and restores what was planned before it opened. This is the explicit Cancel,
   * so it is the one discard that is never asked about: the operator just said to throw the draft
   * away, and the page is told the work went with it.
   */
  cancelEdit(): void {
    this.closeEditor();
    this.notifyPlanningWork();
  }

  /**
   * What is in the editor right now. It is bound as a plain value/input pair rather than through
   * `ngModel`, which applies its first value in a microtask: the editor has to carry the quantity
   * the moment it appears, because that is when it is focused and its value selected.
   *
   * A keystroke that actually changes the quantity is pending planning work, so the page's
   * departure guards are told about it here rather than only once it is committed.
   */
  onEditValueInput(event: Event): void {
    this.editValue = (event.target as HTMLInputElement).value;
    this.notifyPlanningWork();
  }

  /** Enter commits, Escape cancels; the editor is a text box, so neither is the browser's default. */
  onEditKeydown(event: KeyboardEvent, productId: number, cell: PickListMachineQuantity): void {
    if (event.key === 'Enter') {
      event.preventDefault();
      this.commitEdit(productId, cell);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      this.cancelEdit();
    }
  }

  /** Drops one cell's override, putting the backend's own suggestion back. */
  resetOverride(productId: number, machineId: number): void {
    const key = cellKey(productId, machineId);
    if (!this.overrides.delete(key)) {
      return;
    }
    this.completed.delete(key);
    this.notifyPlanningWork();
  }

  // ----- totals, shortages and progress ----------------------------------------------------

  /**
   * One product's storage shortage over the applied run, with the committed overrides in place.
   * This is `GetPickList`'s own rule - `max(0, total to pick - storage)` - recomputed because the
   * server cannot know an override; with none committed it is the figure the server already sent.
   */
  shortageFor(product: PickListProduct): number {
    return Math.max(0, this.plannedUnitsFor(product) - product.storageQuantityInStock);
  }

  /** One product's planned units across the applied machines, overrides included. */
  plannedUnitsFor(product: PickListProduct): number {
    return this.appliedMachineIds.reduce((total, machineId) => {
      const cell = this.cellFor(product, machineId);
      return cell === undefined ? total : total + this.pickQuantity(product.productId, cell);
    }, 0);
  }

  /** How many of one product's picks are still to do, over the applied run. */
  remainingPicksFor(product: PickListProduct): number {
    return this.appliedMachineIds.filter((machineId) => {
      const cell = this.cellFor(product, machineId);
      return cell !== undefined && this.isActionable(product.productId, cell) && !this.isCompleted(product.productId, machineId);
    }).length;
  }

  /** Every pick of the applied run: one per actionable cell, whatever a filter is hiding. */
  totalPickCount(): number {
    return this.actionableCells().length;
  }

  completedPickCount(): number {
    return this.actionableCells().filter((cell) => this.isCompleted(cell.productId, cell.machineId)).length;
  }

  remainingPickCount(): number {
    return this.totalPickCount() - this.completedPickCount();
  }

  /** The whole applied run's units, overrides included - never only the visible rows'. */
  totalUnits(): number {
    return this.pickListProducts.reduce((total, product) => total + this.plannedUnitsFor(product), 0);
  }

  /** Plain language, so progress never has to be read off a bar: "12 of 20 picks completed". */
  progressText(): string {
    return `${this.completedPickCount()} of ${this.totalPickCount()} picks completed`;
  }

  /** How many products of the applied run are short of storage, overrides included. */
  shortageProductCount(): number {
    return this.pickListProducts.filter((product) => this.shortageFor(product) > 0).length;
  }

  /** What the visible rows add up to, stated separately so a filter never looks like the whole run. */
  visibleProductCount(): number {
    return this.visibleProducts().length;
  }

  get filtersActive(): boolean {
    return (
      this.search.trim() !== '' ||
      this.categoryId !== '' ||
      this.onlyRemaining ||
      this.onlyShortages ||
      this.appliedProductIds.length !== this.products.length
    );
  }

  clearViewFilters(): void {
    this.search = '';
    this.categoryId = '';
    this.onlyRemaining = false;
    this.onlyShortages = false;
  }

  // ----- the narrow layout's machine focus -------------------------------------------------

  /** The machine the product cards are focused on, resolved to an applied column. */
  get focusedMachineId(): number | null {
    const columns = this.machineColumnIds;
    if (columns.length === 0) {
      return null;
    }
    return this.focusMachineId !== null && columns.includes(this.focusMachineId) ? this.focusMachineId : columns[0];
  }

  onFocusMachineChange(machineId: number): void {
    this.focusMachineId = machineId;
  }

  /** The focused machine's own picks and units, labelled as that machine's rather than the run's. */
  focusedMachinePickCount(): number {
    const machineId = this.focusedMachineId;
    return machineId === null ? 0 : this.actionableCells().filter((cell) => cell.machineId === machineId).length;
  }

  focusedMachineUnits(): number {
    const machineId = this.focusedMachineId;
    if (machineId === null) {
      return 0;
    }
    return this.pickListProducts.reduce((total, product) => {
      const cell = this.cellFor(product, machineId);
      return cell === undefined ? total : total + this.pickQuantity(product.productId, cell);
    }, 0);
  }

  // ----- loading ---------------------------------------------------------------------------

  /** Re-fetches for the applied machines, or clears the plan locally when none are selected. */
  private fetchOrClear(): void {
    if (this.appliedMachineIds.length === 0) {
      this.appliedToken++;
      this.pickListProducts = [];
      this.lastRefreshedAt = null;
      this.loading = false;
      this.loadFailed = false;
      this.focusMachineId = null;
      this.discardAllPlanningWork();
      return;
    }
    this.fetch([...this.appliedMachineIds]);
  }

  private fetch(machineIds: number[]): void {
    const token = ++this.appliedToken;
    this.loading = true;
    this.loadFailed = false;
    this.pickListService.get(machineIds).subscribe({
      next: (result) => {
        if (token !== this.appliedToken) {
          return;
        }
        this.pickListProducts = result.products;
        this.reconcilePlanningWork();
        this.lastRefreshedAt = new Date();
        this.loading = false;
        this.loadFailed = false;
        this.alignFocusMachine();
      },
      error: (error: unknown) => {
        if (token !== this.appliedToken) {
          return;
        }
        this.loading = false;
        this.loadFailed = true;
        this.loadFailure.emit(error);
      }
    });
  }

  /**
   * Drops any override or mark whose cell no longer exists or no longer has a pick to plan, and
   * closes an editor whose cell the new snapshot does not hold at all: it is not drawn any more, so
   * leaving it open would report work the operator can neither see nor commit.
   */
  private reconcilePlanningWork(): void {
    const live = new Set<string>();
    const present = new Set<string>();
    for (const product of this.pickListProducts) {
      for (const cell of product.machineQuantities) {
        if (!this.appliedMachineIds.includes(cell.machineId)) {
          continue;
        }
        const key = cellKey(product.productId, cell.machineId);
        present.add(key);
        if (cell.quantityToPick > 0) {
          live.add(key);
        }
      }
    }
    for (const key of this.overrides.keys()) {
      if (!live.has(key)) {
        this.overrides.delete(key);
      }
    }
    for (const key of this.completed) {
      if (!live.has(key)) {
        this.completed.delete(key);
      }
    }
    if (this.editingKey !== null && !present.has(this.editingKey)) {
      this.closeEditor();
    }
    this.notifyPlanningWork();
  }

  /** Asks before machine columns holding planning work go; `false` means change nothing at all. */
  private confirmMachineRemoval(machineIds: readonly number[]): boolean {
    const withWork = machineIds.filter((id) => this.machineWorkPresent(id));
    if (withWork.length === 0) {
      return true;
    }
    return window.confirm(discardPlanningWorkOnMachineRemoval(...withWork.map((id) => this.machineLabel(id))));
  }

  /**
   * Whether one machine column holds planning work: a committed override, a checked pick, or the
   * quantity being typed into one of its cells. `false` means removing that column loses nothing.
   */
  private machineWorkPresent(machineId: number): boolean {
    const editing = this.dirtyEditorKey;
    return (
      (editing !== null && keyMachineId(editing) === machineId) ||
      [...this.overrides.keys()].some((key) => keyMachineId(key) === machineId) ||
      [...this.completed].some((key) => keyMachineId(key) === machineId)
    );
  }

  /**
   * Asks before an applied selection discards the quantity still being typed, in the one case
   * {@link confirmMachineRemoval} cannot cover: the editor's own column stays applied and it is the
   * Products selection that leaves its row out. `false` means change nothing at all.
   */
  private confirmOpenEditDiscard(machineIds: readonly number[], productIds: readonly number[]): boolean {
    const editing = this.dirtyEditorKey;
    if (editing === null || !machineIds.includes(keyMachineId(editing)) || productIds.includes(keyProductId(editing))) {
      return true;
    }
    return window.confirm(DISCARD_OPEN_EDIT_ON_APPLY);
  }

  /** Closes the editor when its cell has just left the applied selection, and leaves it alone otherwise. */
  private closeEditorUnlessStillPlanned(): void {
    const editing = this.editingKey;
    if (editing === null) {
      return;
    }
    if (!this.appliedMachineIds.includes(keyMachineId(editing)) || !this.appliedProductIds.includes(keyProductId(editing))) {
      this.closeEditor();
    }
  }

  private discardMachineWork(machineId: number): void {
    for (const key of this.overrides.keys()) {
      if (keyMachineId(key) === machineId) {
        this.overrides.delete(key);
      }
    }
    for (const key of this.completed) {
      if (keyMachineId(key) === machineId) {
        this.completed.delete(key);
      }
    }
    this.notifyPlanningWork();
  }

  /** Drops every kind of planning work this view holds, the quantity being typed included. */
  private discardAllPlanningWork(): void {
    this.closeEditor();
    this.overrides.clear();
    this.completed.clear();
    this.notifyPlanningWork();
  }

  /** Resets the editor's own state. The callers report the planning work they end up holding. */
  private closeEditor(): void {
    this.editingKey = null;
    this.editValue = '';
    this.editBaseline = '';
    this.editError = '';
  }

  private notifyPlanningWork(): void {
    if (this.hasPlanningWork !== this.planningWorkReported) {
      this.planningWorkReported = this.hasPlanningWork;
      this.planningWorkChange.emit(this.planningWorkReported);
    }
  }

  /**
   * Focuses and selects one editor, unless another editor already holds focus - which is how the
   * matrix editor and the card editor, both present in the document, settle which one gets it.
   */
  private focusEditor(input: ElementRef<HTMLInputElement> | undefined): void {
    if (!input) {
      return;
    }
    const active = document.activeElement;
    if (active instanceof HTMLInputElement && active !== input.nativeElement) {
      return;
    }
    input.nativeElement.focus();
    input.nativeElement.select();
  }

  private alignFocusMachine(): void {
    const columns = this.machineColumnIds;
    if (this.focusMachineId !== null && !columns.includes(this.focusMachineId)) {
      this.focusMachineId = null;
    }
  }

  /** Every pick of the applied run, as the cells that hold one. */
  private actionableCells(): { productId: number; machineId: number }[] {
    const cells: { productId: number; machineId: number }[] = [];
    for (const product of this.pickListProducts) {
      for (const cell of product.machineQuantities) {
        if (this.appliedMachineIds.includes(cell.machineId) && this.isActionable(product.productId, cell)) {
          cells.push({ productId: product.productId, machineId: cell.machineId });
        }
      }
    }
    return cells;
  }

  private categoryOf(productId: number): number | undefined {
    return this.products.find((product) => product.id === productId)?.category?.id;
  }

  /** The fleet in site-grouped order, so the Machines filter lists each site's machines together. */
  private orderedMachines(): Machine[] {
    const ordered: Machine[] = [];
    const placed = new Set<Machine>();
    for (const site of this.sites) {
      for (const machine of this.machines) {
        if (machine.siteId != null && machine.siteId === site.siteId) {
          ordered.push(machine);
          placed.add(machine);
        }
      }
    }
    return [...ordered, ...this.machines.filter((machine) => !placed.has(machine))];
  }

  private siteNameOf(machine: Machine): string | null {
    if (machine.siteId == null) {
      return null;
    }
    return this.sites.find((site) => site.siteId === machine.siteId)?.name ?? null;
  }

  /**
   * Product sorts ascending/descending by name; MDB Code sorts numerically, but a missing code
   * (shown as "—") always sorts last in both directions, matching the Needs Ordering page's
   * page-local null policy. Ties fall back to product name, then product id, for a deterministic order.
   */
  private sortProducts(products: PickListProduct[]): PickListProduct[] {
    const column = this.sortColumn;
    const direction = this.sortDirection;
    return [...products].sort((left, right) => {
      if (column === 'mdbCode') {
        const leftCode = left.mdbCode;
        const rightCode = right.mdbCode;
        if (leftCode == null || rightCode == null) {
          if (leftCode == null && rightCode == null) {
            return tieBreak(left, right);
          }
          return leftCode == null ? 1 : -1;
        }

        const comparison = compareSortValues(leftCode, rightCode);
        const resolved = comparison !== 0 ? comparison : tieBreak(left, right);
        return direction === 'desc' ? -resolved : resolved;
      }

      const resolved = tieBreak(left, right);
      return direction === 'desc' ? -resolved : resolved;
    });
  }
}

function tieBreak(left: PickListProduct, right: PickListProduct): number {
  const nameComparison = compareSortValues(left.productName, right.productName);
  return nameComparison !== 0 ? nameComparison : left.productId - right.productId;
}

function cellKey(productId: number, machineId: number): string {
  return `${productId}:${machineId}`;
}

function keyProductId(key: string): number {
  return Number(key.split(':')[0]);
}

function keyMachineId(key: string): number {
  return Number(key.split(':')[1]);
}

function sameIds(a: readonly number[], b: readonly number[]): boolean {
  if (a.length !== b.length) {
    return false;
  }
  const bSet = new Set(b);
  return a.every((id) => bSet.has(id));
}
