import { Component, HostListener, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { EMPTY, catchError, tap } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { PickListService } from '../../services/pick-list.service';
import { SiteService } from '../../services/site.service';
import { ToastService } from '../../services/toast.service';
import { Machine, PickListMachineQuantity, PickListProduct, Product, SiteListItem } from '../../models/models';
import { ListLoadState } from '../shared/list-load-state';
import { MultiSelectDropdownComponent, MultiSelectOption } from '../shared/multi-select-dropdown.component';
import { IconComponent } from '../shared/icon.component';
import { BusinessDateTimePipe } from '../../formatting/business-date-time.pipe';
import { PERMISSION_NOT_GRANTED_REASON, nayaxUnavailableMessage } from '../../provider/nayax-availability';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { loadProviderList } from '../../provider/provider-list-load';
import { SortDirection, compareSortValues, nextSortState, sortAriaState, sortColumnLabel } from '../shared/sortable-column';
import { ManualPickListCatalogueState, ManualPickListComponent } from './manual-pick-list.component';
import { refillCellKey } from './pick-list-refill-errors';
import {
  parsePickQuantity,
  picksCompletedLabel,
  planCellKey,
  planCellMachineId,
  storageShortage
} from './plan-picks';
import { PendingWorkGuarded } from './pick-list-leave.guard';

type PickListSortColumn = 'name' | 'mdbCode';

/** Which of the page's two views is showing: Plan picks (`'calculated'`) or Record refill (`'manual'`). */
export type PickListMode = 'calculated' | 'manual';

/** The Plan picks status filter: every row, rows with a pick still to check, or rows storage cannot cover. */
export type PlanStatusFilter = 'all' | 'remaining' | 'shortages';

export const PLAN_PICKS_INVENTORY_IMPACT = 'Planning only. Editing quantities and checking picks does not change inventory.';

export const RECORD_REFILL_INVENTORY_IMPACT = 'Recording this refill reduces storage stock by the quantities entered.';

export const DISCARD_PLAN_ON_REFRESH_CONFIRMATION =
  'Refreshing loads a new snapshot and discards your edited pick quantities and checked picks. Continue?';

export const DISCARD_PLAN_ON_RESET_CONFIRMATION = 'Reset clears the machine selection, your edited pick quantities and checked picks. Continue?';

export function discardPlanMachineConfirmation(...machineNames: string[]): string {
  const columns = machineNames.length === 1 ? 'machine' : 'machines';
  return `Remove ${machineNames.join(', ')}? The edited pick quantities and checked picks for that ${columns} will be discarded.`;
}

export const UNSAVED_WORK_LEAVE_WARNING = 'You have unsaved Pick List work. Leave this page and discard it?';

export const UNRESOLVED_SAVE_LEAVE_WARNING =
  "A refill save could not be confirmed. If you leave, its retry is lost and you will not be able to safely resend it. Leave anyway?";

/** The planning state of the one cell being edited: its key, the raw text in its box, and that text's error. */
/** One run of matrix columns under a shared site header; `siteId` is the grouping key, `siteName` only its label. */
export interface ColumnGroup {
  siteId: number | null;
  siteName: string | null;
  machineIds: number[];
}

interface PickEdit {
  readonly key: string;
  /** The committed quantity the editor opened with, as text; the edit is unsaved work only once `value` differs from it. */
  readonly initial: string;
  value: string;
  error: string | null;
}

/** Below this width the page shows product cards with machine focus instead of the matrix. */
const COMPACT_LAYOUT_QUERY = '(max-width: 767px)';

/**
 * Pick List page (issues #222, #226, #744; redesigned as **Plan picks** and **Record refill** by
 * issue #996).
 *
 * **Plan picks** is a matrix built on the #221 backend projection. Product and machine selection is
 * staged in the toolbar's dropdowns and only takes effect on "Apply"; a separate **Refresh** reads a
 * new snapshot for the machines already applied, even when nothing changed, and is also the Retry
 * after a failed read. Planning is entirely frontend, in-memory state and never calls a mutation
 * endpoint: an operator may **edit a suggested pick quantity** - an override kept beside the
 * original suggestion, never a change to stock or to a target - and **check a pick off**.
 * Committing a changed quantity clears that cell's checkmark, and a committed `0` is a deliberate
 * skip that no longer counts as a pick. Totals, storage shortages and progress are computed from
 * the committed quantities with the backend projection's own formulas (`total = Σ picks`,
 * `shortage = max(0, total - storage)`), so with no override they equal the backend's figures.
 * Filtering, sorting and switching the focused machine on a phone never touch that state; a new
 * snapshot (Refresh, or an Apply that adds machines), Reset and removing a machine discard it, and
 * each asks first while there is anything to lose. Every read carries a generation, so a response
 * that arrives after the selection was reset or changed is dropped rather than resurrecting a plan.
 *
 * **The whole Plan picks view is a live fleet read**, so without a usable provider there is nothing
 * to plan (issue #728): see {@link machinesUnavailable} and {@link machinesError}. Machines are
 * grouped by site where the light site list names one; a machine with no known site is still
 * listed, just without a site heading.
 *
 * **Record refill** is `ManualPickListComponent` (issue #744, #904), composed in rather than grown
 * inline (docs/architecture.md § Page composition boundary). With a usable provider and the
 * permissions Plan picks needs both views are offered and {@link mode} defaults to `'calculated'`;
 * without either, {@link calculatedUnavailableReason} is non-empty and the page opens straight into
 * Record refill and offers no toggle. The suggestions Record refill may show beside an entry are
 * the applied snapshot's own suggestions, translated to local machine ids ({@link refillSuggestions});
 * no planned quantity is ever copied into an entry.
 *
 * **A mode switch never destroys Record refill, because it can hold an unconfirmed write**; it is
 * created the first time it is shown ({@link manualViewCreated}) and hidden rather than destroyed
 * afterwards, and while a save is in flight ({@link manualSaveInFlight}) switching away is refused.
 *
 * **Leaving the page is guarded** ({@link confirmLeave}, wired as the route's `canDeactivate`, and
 * {@link onBeforeUnload} for a reload or a closed tab): typed refill quantities, edited or checked
 * planning work, and above all an unconfirmed save - whose retry context leaving destroys - all ask
 * first. A cancelled departure changes nothing.
 *
 * **A machine-details page can open straight into a preselected Record refill**: its link is this
 * route with `?mode=manual&machineId=<local id>`, read once from the route snapshot in `ngOnInit`.
 */
@Component({
  selector: 'app-pick-list',
  standalone: true,
  imports: [CommonModule, FormsModule, MultiSelectDropdownComponent, BusinessDateTimePipe, ManualPickListComponent, IconComponent],
  templateUrl: './pick-list.component.html'
})
export class PickListComponent implements OnInit, OnDestroy, PendingWorkGuarded {
  allMachines: Machine[] = [];
  allProducts: Product[] = [];

  /** The light site list, used only to name and group machines; empty when it is unknown. */
  sites: SiteListItem[] = [];

  /** The active catalogue reads: the first one, its retries, and the refresh after each recorded refill. */
  readonly productsLoadState = new ListLoadState();

  /** Whether a catalogue read has ever succeeded, so a later failure is a failed refresh of real data rather than no data at all. */
  private productsEverLoaded = false;

  /** Which view is showing; see the class doc for when it is forced to `'manual'`. */
  mode: PickListMode = 'calculated';

  /** Whether `ManualPickListComponent` exists yet; once created it is kept alive, hidden while Plan picks shows. */
  manualViewCreated = false;

  /** Whether Record refill has a save outstanding; while it has, switching away is refused. */
  manualSaveInFlight = false;

  /** The one sentence saying Nayax has refused the `machineStock` permission Plan picks needs; `''` while it has not. */
  machineProductsUnavailable = '';

  /** Staged filter choices from the dropdowns, not yet applied to the matrix. */
  stagedMachineIds: number[] = [];
  stagedProductIds: number[] = [];

  /** The last-applied filter choices: what the matrix columns/rows and `pickListProducts` reflect. */
  appliedMachineIds: number[] = [];
  appliedProductIds: number[] = [];

  /** The last-fetched read-only projection for `appliedMachineIds`. */
  pickListProducts: PickListProduct[] = [];

  /** Live, unstaged view filters: they change which rows show, never the applied run. */
  search = '';
  categoryId: number | '' = '';
  statusFilter: PlanStatusFilter = 'all';

  /** The machine a phone's product cards are focused on; `null` falls back to the first applied machine. */
  focusedMachineId: number | null = null;

  /** Whether the page is showing its phone layout (cards and machine focus) rather than the matrix. */
  compactLayout = false;

  /** Committed planning overrides, keyed by {@link planCellKey}. A cell without one picks its suggestion. Never persisted. */
  private readonly overrides = new Map<string, number>();

  /** Checked-off picks, keyed by {@link planCellKey}. Never persisted. */
  private readonly pickedCells = new Set<string>();

  /** The one cell whose quantity is being edited, if any. */
  edit: PickEdit | null = null;

  /** Client-side only: until a header is clicked, rows stay in the backend's default order. */
  sortColumn: PickListSortColumn | null = null;
  sortDirection: SortDirection = 'asc';

  lastRefreshedAt: Date | null = null;
  readonly loadState = new ListLoadState();

  /**
   * Bumped whenever the plan is cleared or replaced, so a read that was already in flight cannot
   * land on a selection it was not asked for; see {@link fetchSnapshot}.
   */
  private snapshotGeneration = 0;

  /** The one sentence for a provider that cannot list machines; `''` while it can. */
  machinesUnavailable = '';

  /** Set when the machine list could not be read for a reason that is not an availability answer. */
  machinesError = '';

  /** The "Record refill" link's `machineId` query parameter, or `null` for ordinary pick-list entry. */
  preselectedMachineId: number | null = null;

  /**
   * The suggestions Record refill may show beside an entry: the applied snapshot's suggested
   * quantities, keyed by the **local** machine id each provider machine maps to. A machine with no
   * local record contributes nothing, and these are the original suggestions, never planned
   * overrides. Rebuilt only when the snapshot or the applied machines change, so the same map is
   * handed down between changes.
   */
  refillSuggestions: ReadonlyMap<string, number> = new Map();

  readonly planInventoryImpact = PLAN_PICKS_INVENTORY_IMPACT;
  readonly refillInventoryImpact = RECORD_REFILL_INVENTORY_IMPACT;

  @ViewChild(ManualPickListComponent) private readonly manualView?: ManualPickListComponent;

  private compactQuery: MediaQueryList | null = null;
  private readonly onCompactChange = (event: MediaQueryListEvent): void => {
    this.compactLayout = event.matches;
  };

  constructor(
    private readonly machineService: MachineService,
    private readonly productService: ProductService,
    private readonly pickListService: PickListService,
    private readonly toastService: ToastService,
    private readonly provider: ProviderAvailabilityService,
    private readonly route: ActivatedRoute,
    private readonly siteService: SiteService
  ) {}

  ngOnInit(): void {
    const queryParams = this.route.snapshot.queryParamMap;
    if (queryParams.get('mode') === 'manual') {
      this.setMode('manual');
    }
    const linkedMachineId = Number(queryParams.get('machineId'));
    this.preselectedMachineId = queryParams.get('machineId') !== null && Number.isInteger(linkedMachineId) && linkedMachineId > 0
      ? linkedMachineId
      : null;

    this.observeLayout();

    loadProviderList(this.provider, 'machineFleet', () => this.machineService.getAll()).subscribe((outcome) => {
      this.allMachines = outcome.kind === 'listed' ? outcome.items : [];
      this.machinesUnavailable =
        outcome.kind === 'unavailable' ? nayaxUnavailableMessage(outcome.unavailability, 'The pick list') : '';
      this.machinesError =
        outcome.kind === 'failed' ? 'The machine list could not be loaded, so a pick list cannot be built.' : '';
      if (this.machinesUnavailable !== '') {
        this.setMode('manual');
      }
      this.updateRefillSuggestions();
      if (outcome.kind === 'listed' && outcome.items.some((m) => m.siteId != null)) {
        this.loadSites();
      }
    });
    // Checked up front, beside the machine fleet above: a usable connection does not mean Nayax has
    // granted every read Plan picks needs, and a permission this session already knows is refused
    // must open straight into Record refill rather than offering a matrix that will only fail.
    this.provider.featureAvailability('machineStock').subscribe((availability) => {
      if (availability === 'permissionNotGranted') {
        this.machineProductsUnavailable = nayaxUnavailableMessage(PERMISSION_NOT_GRANTED_REASON, 'The calculated pick list');
        this.setMode('manual');
      }
    });
    this.loadProducts();
  }

  ngOnDestroy(): void {
    this.compactQuery?.removeEventListener('change', this.onCompactChange);
  }

  /**
   * Reads the active catalogue: on arrival, after each recorded refill, and whenever the operator
   * retries a failed read. Only the first successful read seeds the Products filter's selection.
   */
  loadProducts(): void {
    const token = this.productsLoadState.start();
    this.productService.getAll().subscribe({
      next: (products) => {
        if (this.productsLoadState.isCurrent(token)) {
          this.allProducts = products;
          if (!this.productsEverLoaded) {
            const allProductIds = products.map((p) => p.id);
            this.stagedProductIds = allProductIds;
            this.appliedProductIds = allProductIds;
          }
          this.productsEverLoaded = true;
        }
        this.productsLoadState.succeed(token);
      },
      error: () => {
        this.productsLoadState.fail(token);
        this.toastService.error(this.productsEverLoaded ? 'Failed to refresh the product list.' : 'Failed to load the product list.');
      }
    });
  }

  /** What Record refill may say about {@link allProducts}; see `ManualPickListCatalogueState`. */
  get catalogueState(): ManualPickListCatalogueState {
    if (this.productsEverLoaded) {
      return 'loaded';
    }
    return this.productsLoadState.loadFailed ? 'failed' : 'loading';
  }

  /** Whether the last catalogue read failed after an earlier one succeeded, so the storage quantities shown may be stale. */
  get catalogueRefreshFailed(): boolean {
    return this.productsEverLoaded && this.productsLoadState.loadFailed;
  }

  /** The one sentence saying why Plan picks is not a real choice right now, or `''` while it is. */
  get calculatedUnavailableReason(): string {
    return this.machinesUnavailable || this.machineProductsUnavailable;
  }

  /**
   * Switches the visible view. Switching to Plan picks is refused once
   * {@link calculatedUnavailableReason} is set and while {@link manualSaveInFlight} is set.
   */
  switchMode(mode: PickListMode): void {
    if (mode === 'calculated' && (this.calculatedUnavailableReason !== '' || this.manualSaveInFlight)) {
      return;
    }
    this.setMode(mode);
  }

  /** Refreshes the active catalogue Record refill reads, after it records a refill. */
  refreshProductsAfterManualRefill(): void {
    this.loadProducts();
  }

  /** Records whether Record refill has a save outstanding; see {@link switchMode}. */
  onManualSaveInFlightChange(inFlight: boolean): void {
    this.manualSaveInFlight = inFlight;
  }

  /** The one place `mode` is written, so entering Record refill always creates the view that must then survive. */
  private setMode(mode: PickListMode): void {
    this.mode = mode;
    if (mode === 'manual') {
      this.manualViewCreated = true;
    }
  }

  /** Whether the machine selection and the matrix are offered at all. */
  get machineSelectionShown(): boolean {
    return this.machinesUnavailable === '' && this.machinesError === '';
  }

  /** The machines to choose from, grouped under their site's name where the site list names one. */
  machineOptions(): MultiSelectOption[] {
    const options = this.allMachines.map((m) => ({ id: m.machineID, label: this.machineLabel(m.machineID), group: this.siteName(m) }));
    if (!options.some((o) => o.group !== undefined)) {
      return options.map(({ id, label }) => ({ id, label }));
    }
    const sited = options.filter((o) => o.group !== undefined).sort((a, b) => compareSortValues(a.group!, b.group!));
    const unsited = options.filter((o) => o.group === undefined).map((o) => ({ ...o, group: 'Other machines' }));
    return [...sited, ...unsited];
  }

  productOptions(): MultiSelectOption[] {
    return this.allProducts.map((p) => ({ id: p.id, label: p.name }));
  }

  categories(): { id: number; name: string }[] {
    const byId = new Map<number, { id: number; name: string }>();
    for (const product of this.allProducts) {
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
    const machine = this.allMachines.find((m) => m.machineID === machineId);
    if (!machine) {
      return `Machine #${machineId}`;
    }
    return machine.machineName || machine.machineNumber || `Machine #${machineId}`;
  }

  /**
   * The applied machines as matrix columns, grouped so machines at the same site are adjacent under
   * one shared header. Groups are keyed by the site's id, so two sites that happen to share a name
   * stay separate; the name is only what the header shows. A group's `siteId` and `siteName` are
   * `null` for machines with no known site; nothing is invented for them. Groups keep the order
   * their first machine was selected in.
   */
  columnGroups(): ColumnGroup[] {
    const groups: ColumnGroup[] = [];
    for (const machineId of this.appliedMachineIds) {
      const machine = this.allMachines.find((m) => m.machineID === machineId);
      const site = machine ? this.siteFor(machine) : undefined;
      const siteId = site?.siteId ?? null;
      const group = groups.find((g) => g.siteId === siteId);
      if (group) {
        group.machineIds.push(machineId);
      } else {
        groups.push({ siteId, siteName: site?.name ?? null, machineIds: [machineId] });
      }
    }
    const sited = groups.filter((g) => g.siteId !== null);
    const unsited = groups.filter((g) => g.siteId === null);
    return [...sited, ...unsited];
  }

  /** The applied machines in column order: {@link columnGroups}, flattened. */
  columnMachineIds(): number[] {
    return this.columnGroups().flatMap((g) => g.machineIds);
  }

  /** Whether any applied machine has a known site, so the matrix shows a site header row. */
  get siteHeadersShown(): boolean {
    return this.columnGroups().some((g) => g.siteId !== null);
  }

  /**
   * Applies the staged Products/Machines selections together. A product-only change, or a machine
   * selection that only drops machines, reuses the loaded snapshot; a selection that adds a machine
   * needs a new snapshot, which discards the plan - so it asks first while there is planning work.
   * Cancel changes nothing, not even the product filter.
   */
  applyFilters(): void {
    const removed = this.appliedMachineIds.filter((id) => !this.stagedMachineIds.includes(id));
    const added = this.stagedMachineIds.filter((id) => !this.appliedMachineIds.includes(id));
    const needsSnapshot = added.length > 0 && this.stagedMachineIds.length > 0;

    if (needsSnapshot) {
      if (this.hasPlanningWork() && !window.confirm(DISCARD_PLAN_ON_REFRESH_CONFIRMATION)) {
        return;
      }
    } else if (!this.confirmMachineRemoval(removed)) {
      return;
    }

    this.appliedProductIds = [...this.stagedProductIds];
    this.appliedMachineIds = [...this.stagedMachineIds];

    if (needsSnapshot) {
      this.discardPlanningWork();
      this.fetchSnapshot();
    } else if (removed.length > 0) {
      this.afterMachinesRemoved(removed);
    }
  }

  /** Removes one machine chip from both the applied and staged selections, asking first when it holds planning work. */
  removeMachine(machineId: number): void {
    if (!this.confirmMachineRemoval([machineId])) {
      return;
    }
    this.appliedMachineIds = this.appliedMachineIds.filter((id) => id !== machineId);
    this.stagedMachineIds = this.stagedMachineIds.filter((id) => id !== machineId);
    this.afterMachinesRemoved([machineId]);
  }

  /**
   * Reads a new snapshot for the machines already applied, even when nothing about the selection
   * changed - this is also the Retry after a failed read. Planning work belongs to the snapshot it
   * was made against, so it is discarded, after asking.
   */
  refresh(): void {
    if (this.appliedMachineIds.length === 0) {
      return;
    }
    if (this.hasPlanningWork() && !window.confirm(DISCARD_PLAN_ON_REFRESH_CONFIRMATION)) {
      return;
    }
    this.discardPlanningWork();
    this.fetchSnapshot();
  }

  resetAll(): void {
    if (this.hasPlanningWork() && !window.confirm(DISCARD_PLAN_ON_RESET_CONFIRMATION)) {
      return;
    }
    const allProductIds = this.allProducts.map((p) => p.id);
    this.stagedProductIds = allProductIds;
    this.appliedProductIds = allProductIds;
    this.stagedMachineIds = [];
    this.appliedMachineIds = [];
    this.search = '';
    this.categoryId = '';
    this.statusFilter = 'all';
    this.focusedMachineId = null;
    this.clearSnapshot();
    this.sortColumn = null;
    this.sortDirection = 'asc';
  }

  /** The applied run: the applied products that have a cell at an applied machine, in backend order. */
  runProducts(): PickListProduct[] {
    return this.pickListProducts.filter(
      (p) => this.appliedProductIds.includes(p.productId) && p.machineQuantities.some((mq) => this.appliedMachineIds.includes(mq.machineId))
    );
  }

  /** The rows on screen: the applied run, narrowed by the live search, category and status filters, then sorted. */
  visibleProducts(): PickListProduct[] {
    const term = this.search.trim().toLowerCase();
    const filtered = this.runProducts().filter((p) => {
      if (term !== '' && !p.productName.toLowerCase().includes(term) && !String(p.mdbCode ?? '').includes(term)) {
        return false;
      }
      if (this.categoryId !== '' && this.productCategoryId(p.productId) !== this.categoryId) {
        return false;
      }
      if (this.statusFilter === 'remaining') {
        return this.remainingPicksFor(p) > 0;
      }
      if (this.statusFilter === 'shortages') {
        return this.productShortage(p) > 0;
      }
      return true;
    });
    return this.sortColumn === null ? filtered : this.sortProducts(filtered);
  }

  /** On a phone: the visible rows that have a cell at the focused machine. */
  focusedProducts(): PickListProduct[] {
    const machineId = this.focusedMachine();
    return machineId === null ? [] : this.visibleProducts().filter((p) => this.cellFor(p, machineId) !== undefined);
  }

  /** The machine a phone's cards show, which is always one of the applied machines. */
  focusedMachine(): number | null {
    if (this.focusedMachineId !== null && this.appliedMachineIds.includes(this.focusedMachineId)) {
      return this.focusedMachineId;
    }
    return this.columnMachineIds()[0] ?? null;
  }

  focusMachine(machineId: number): void {
    this.focusedMachineId = machineId;
  }

  sortBy(column: PickListSortColumn): void {
    const next = nextSortState(this.sortColumn, this.sortDirection, column);
    this.sortColumn = next.column;
    this.sortDirection = next.direction;
  }

  ariaSort(column: PickListSortColumn): 'ascending' | 'descending' | 'none' {
    return sortAriaState(this.sortColumn, this.sortDirection, column);
  }

  sortLabel(label: string, column: PickListSortColumn): string {
    return sortColumnLabel(label, this.sortColumn, this.sortDirection, column);
  }

  cellFor(product: PickListProduct, machineId: number): PickListMachineQuantity | undefined {
    return product.machineQuantities.find((m) => m.machineId === machineId);
  }

  /** Whether a cell is a suggested pick, which is what makes it editable and checkable at all. */
  isSuggestedPick(cell: PickListMachineQuantity | undefined): cell is PickListMachineQuantity {
    return cell !== undefined && cell.quantityToPick > 0;
  }

  /** The quantity the plan picks for a cell: its committed override, else the suggestion. */
  plannedQuantity(productId: number, cell: PickListMachineQuantity): number {
    return this.overrides.get(planCellKey(productId, cell.machineId)) ?? cell.quantityToPick;
  }

  /** Whether a cell's committed quantity differs from its suggestion. */
  isOverridden(productId: number, machineId: number): boolean {
    return this.overrides.has(planCellKey(productId, machineId));
  }

  isPicked(productId: number, machineId: number): boolean {
    return this.pickedCells.has(planCellKey(productId, machineId));
  }

  /**
   * Toggles a pick's checkmark. Only a suggested cell whose committed quantity is above zero is a
   * pick; a zero-suggestion cell and a committed skip never toggle.
   */
  togglePicked(product: PickListProduct, machineId: number): void {
    const cell = this.cellFor(product, machineId);
    if (!this.isSuggestedPick(cell) || this.plannedQuantity(product.productId, cell) <= 0) {
      return;
    }
    const key = planCellKey(product.productId, machineId);
    if (this.pickedCells.has(key)) {
      this.pickedCells.delete(key);
    } else {
      this.pickedCells.add(key);
    }
  }

  /** Opens a suggested cell's quantity for editing, starting from its committed quantity. */
  startEdit(product: PickListProduct, machineId: number): void {
    const cell = this.cellFor(product, machineId);
    if (!this.isSuggestedPick(cell)) {
      return;
    }
    const key = planCellKey(product.productId, machineId);
    const initial = String(this.plannedQuantity(product.productId, cell));
    this.edit = { key, initial, value: initial, error: null };
    setTimeout(() => {
      const input = document.getElementById(this.editInputId(product.productId, machineId)) as HTMLInputElement | null;
      input?.focus();
      input?.select();
    });
  }

  isEditing(productId: number, machineId: number): boolean {
    return this.edit?.key === planCellKey(productId, machineId);
  }

  onEditInput(value: string): void {
    if (this.edit) {
      this.edit.value = value;
      this.edit.error = null;
    }
  }

  /** Steps the quantity being edited by one, never below zero; malformed text is left for the operator to fix. */
  stepEdit(delta: number): void {
    if (!this.edit) {
      return;
    }
    const parsed = parsePickQuantity(this.edit.value);
    if ('error' in parsed) {
      this.edit.error = parsed.error;
      return;
    }
    this.onEditInput(String(Math.max(0, parsed.quantity + delta)));
  }

  /**
   * Commits the quantity being edited, or keeps the box open with an inline message when it is not
   * a nonnegative whole number the pick list can represent. A committed value equal to the
   * suggestion drops the override; a changed quantity clears the cell's checkmark.
   */
  commitEdit(product: PickListProduct, machineId: number): void {
    const cell = this.cellFor(product, machineId);
    if (!this.edit || !this.isEditing(product.productId, machineId) || !this.isSuggestedPick(cell)) {
      return;
    }
    const parsed = parsePickQuantity(this.edit.value);
    if ('error' in parsed) {
      this.edit.error = parsed.error;
      return;
    }
    this.setPlannedQuantity(product.productId, cell, parsed.quantity);
    this.edit = null;
  }

  /** Abandons the edit; the committed quantity is exactly what it was. */
  cancelEdit(): void {
    this.edit = null;
  }

  /** Puts a cell back on its suggestion. */
  useSuggestion(product: PickListProduct, machineId: number): void {
    const cell = this.cellFor(product, machineId);
    if (this.isSuggestedPick(cell)) {
      this.setPlannedQuantity(product.productId, cell, cell.quantityToPick);
    }
  }

  editInputId(productId: number, machineId: number): string {
    return `plan-pick-edit-${productId}-${machineId}`;
  }

  editErrorId(productId: number, machineId: number): string {
    return `plan-pick-edit-error-${productId}-${machineId}`;
  }

  /** The units the plan takes from storage for one product, across every applied machine. */
  productPlannedTotal(product: PickListProduct): number {
    return product.machineQuantities
      .filter((mq) => this.appliedMachineIds.includes(mq.machineId))
      .reduce((sum, mq) => sum + this.plannedQuantity(product.productId, mq), 0);
  }

  /** The units the plan needs beyond what storage holds, across every applied machine. */
  productShortage(product: PickListProduct): number {
    return storageShortage(this.productPlannedTotal(product), product.storageQuantityInStock);
  }

  /** The whole applied run's planned units, whatever the view filters hide. */
  totalToPickUnits(): number {
    return this.runProducts().reduce((sum, p) => sum + this.productPlannedTotal(p), 0);
  }

  /** The whole applied run's picks: suggested cells whose committed quantity is above zero. A skip is not a pick. */
  actionableCellCount(): number {
    return this.actionableCells(this.runProducts()).length;
  }

  pickedActionableCellCount(): number {
    return this.actionableCells(this.runProducts()).filter((c) => this.isPicked(c.productId, c.machineId)).length;
  }

  remainingPickCount(): number {
    return this.actionableCellCount() - this.pickedActionableCellCount();
  }

  progressPercent(): number {
    const total = this.actionableCellCount();
    return total === 0 ? 0 : Math.round((this.pickedActionableCellCount() / total) * 100);
  }

  /** "12 of 20 picks completed" for the whole applied run. */
  runProgressLabel(): string {
    return picksCompletedLabel(this.pickedActionableCellCount(), this.actionableCellCount());
  }

  /** The products of the whole applied run whose planned total exceeds storage. */
  shortageProducts(): PickListProduct[] {
    return this.runProducts().filter((p) => this.productShortage(p) > 0);
  }

  /** How many of the run's products the status filter would show. */
  remainingProductCount(): number {
    return this.runProducts().filter((p) => this.remainingPicksFor(p) > 0).length;
  }

  /** "4 of 5 picks completed" for one machine, across the whole applied run. */
  machineProgressLabel(machineId: number): string {
    const cells = this.actionableCells(this.runProducts()).filter((c) => c.machineId === machineId);
    return picksCompletedLabel(cells.filter((c) => this.isPicked(c.productId, c.machineId)).length, cells.length);
  }

  /** The planned units for one machine, across the whole applied run. */
  machinePlannedUnits(machineId: number): number {
    return this.runProducts().reduce((sum, p) => {
      const cell = this.cellFor(p, machineId);
      return cell ? sum + this.plannedQuantity(p.productId, cell) : sum;
    }, 0);
  }

  /**
   * Whether the plan holds anything an operator would mind losing: an edited quantity, a checked
   * pick, or an open quantity editor whose text was changed but not yet committed.
   */
  hasPlanningWork(): boolean {
    return this.overrides.size > 0 || this.pickedCells.size > 0 || this.editIsDirty();
  }

  /** Whether the open quantity editor holds text other than the quantity it opened with; an untouched editor loses nothing. */
  editIsDirty(): boolean {
    return this.edit !== null && this.edit.value.trim() !== this.edit.initial;
  }

  /** The departure warning that applies right now, or `null` when leaving loses nothing. */
  leaveWarning(): string | null {
    if (this.manualSaveInFlight || this.manualView?.unresolvedSubmission) {
      return UNRESOLVED_SAVE_LEAVE_WARNING;
    }
    if (this.manualView?.hasEnteredQuantities() || this.hasPlanningWork()) {
      return UNSAVED_WORK_LEAVE_WARNING;
    }
    return null;
  }

  /** The route's `canDeactivate` answer: leave freely, or only once the operator confirms. */
  confirmLeave(): boolean {
    const warning = this.leaveWarning();
    return warning === null || window.confirm(warning);
  }

  /** Asks the browser to confirm a reload or a closed tab while there is work to lose; the browser words the question itself. */
  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.leaveWarning() !== null) {
      event.preventDefault();
    }
  }

  private updateRefillSuggestions(): void {
    const suggestions = new Map<string, number>();
    for (const product of this.pickListProducts) {
      for (const cell of product.machineQuantities) {
        const localId = this.allMachines.find((m) => m.machineID === cell.machineId)?.localMachineId;
        if (localId != null && this.appliedMachineIds.includes(cell.machineId)) {
          suggestions.set(refillCellKey(product.productId, localId), cell.quantityToPick);
        }
      }
    }
    this.refillSuggestions = suggestions;
  }

  private loadSites(): void {
    this.siteService.getList().subscribe({
      next: (sites) => (this.sites = sites),
      // The site list only names and groups machines; without it they are listed ungrouped.
      error: () => (this.sites = [])
    });
  }

  private siteName(machine: Machine): string | undefined {
    return this.siteFor(machine)?.name;
  }

  /** The machine's site from the site list, or `undefined` when it has none or the list does not know it. */
  private siteFor(machine: Machine): SiteListItem | undefined {
    if (machine.siteId == null) {
      return undefined;
    }
    return this.sites.find((s) => s.siteId === machine.siteId);
  }

  private productCategoryId(productId: number): number | undefined {
    return this.allProducts.find((p) => p.id === productId)?.category?.id;
  }

  private remainingPicksFor(product: PickListProduct): number {
    return this.actionableCells([product]).filter((c) => !this.isPicked(c.productId, c.machineId)).length;
  }

  private setPlannedQuantity(productId: number, cell: PickListMachineQuantity, quantity: number): void {
    const key = planCellKey(productId, cell.machineId);
    if (quantity !== this.plannedQuantity(productId, cell)) {
      this.pickedCells.delete(key);
    }
    if (quantity === cell.quantityToPick) {
      this.overrides.delete(key);
    } else {
      this.overrides.set(key, quantity);
    }
  }

  /** Asks before removing machines that hold planning work; `true` when there is nothing to lose or the operator agreed. */
  private confirmMachineRemoval(machineIds: number[]): boolean {
    const withWork = machineIds.filter((id) => this.machineHasPlanningWork(id));
    return withWork.length === 0 || window.confirm(discardPlanMachineConfirmation(...withWork.map((id) => this.machineLabel(id))));
  }

  private machineHasPlanningWork(machineId: number): boolean {
    const dirtyEditKeys = this.edit && this.editIsDirty() ? [this.edit.key] : [];
    return [...this.overrides.keys(), ...this.pickedCells, ...dirtyEditKeys].some((key) => planCellMachineId(key) === machineId);
  }

  /**
   * Drops the removed machines' planning work. The loaded snapshot still covers every remaining
   * machine, so no new read is needed - unless one is still in flight for the old selection, which
   * is then superseded by a read of the new one.
   */
  private afterMachinesRemoved(machineIds: number[]): void {
    for (const key of this.overrides.keys()) {
      if (machineIds.includes(planCellMachineId(key))) {
        this.overrides.delete(key);
      }
    }
    for (const key of this.pickedCells) {
      if (machineIds.includes(planCellMachineId(key))) {
        this.pickedCells.delete(key);
      }
    }
    if (this.edit && machineIds.includes(planCellMachineId(this.edit.key))) {
      this.edit = null;
    }
    if (this.appliedMachineIds.length === 0) {
      this.clearSnapshot();
    } else if (!this.loadState.loaded && !this.loadState.loadFailed) {
      this.fetchSnapshot();
    }
    this.updateRefillSuggestions();
  }

  private discardPlanningWork(): void {
    this.overrides.clear();
    this.pickedCells.clear();
    this.edit = null;
  }

  /** Empties the plan and supersedes any read still in flight, so it cannot bring the plan back. */
  private clearSnapshot(): void {
    this.snapshotGeneration++;
    this.pickListProducts = [];
    this.discardPlanningWork();
    this.lastRefreshedAt = null;
    this.updateRefillSuggestions();
  }

  /**
   * Reads the live Nayax-backed projection for the applied machines. Only the latest read for the
   * latest selection may land: a superseded read - an older request, or one made before the plan
   * was cleared - is dropped, failures included.
   */
  private fetchSnapshot(): void {
    const generation = ++this.snapshotGeneration;
    const machineIds = [...this.appliedMachineIds];
    const token = this.loadState.start();
    const isLatest = (): boolean => generation === this.snapshotGeneration && this.loadState.isCurrent(token);
    this.pickListService.get(machineIds).pipe(
      tap((result) => {
        if (isLatest()) {
          this.pickListProducts = result.products;
          this.lastRefreshedAt = new Date();
          this.updateRefillSuggestions();
        }
        this.loadState.succeed(token);
      }),
      catchError((error: unknown) => {
        this.loadState.fail(token);
        if (!isLatest()) {
          return EMPTY;
        }
        // The permission `machineStock` evidence above is read once at init; a session's first
        // concrete proof that it is refused is often this very read, so it is recorded here too.
        const unavailability = this.provider.noteRefusal(error, 'machineStock');
        if (unavailability !== null) {
          this.machineProductsUnavailable = nayaxUnavailableMessage(unavailability, 'The calculated pick list');
          this.setMode('manual');
        } else {
          this.toastService.error('Failed to load the pick list.');
        }
        return EMPTY;
      })
    ).subscribe();
  }

  private actionableCells(products: PickListProduct[]): { productId: number; machineId: number }[] {
    const cells: { productId: number; machineId: number }[] = [];
    for (const product of products) {
      for (const mq of product.machineQuantities) {
        if (mq.quantityToPick > 0 && this.plannedQuantity(product.productId, mq) > 0 && this.appliedMachineIds.includes(mq.machineId)) {
          cells.push({ productId: product.productId, machineId: mq.machineId });
        }
      }
    }
    return cells;
  }

  private observeLayout(): void {
    const query = typeof window !== 'undefined' ? window.matchMedia?.(COMPACT_LAYOUT_QUERY) : undefined;
    if (!query) {
      return;
    }
    this.compactQuery = query;
    this.compactLayout = query.matches;
    query.addEventListener('change', this.onCompactChange);
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
            return this.tieBreak(left, right);
          }
          return leftCode == null ? 1 : -1;
        }

        const comparison = compareSortValues(leftCode, rightCode);
        const resolved = comparison !== 0 ? comparison : this.tieBreak(left, right);
        return direction === 'desc' ? -resolved : resolved;
      }

      const resolved = this.tieBreak(left, right);
      return direction === 'desc' ? -resolved : resolved;
    });
  }

  private tieBreak(left: PickListProduct, right: PickListProduct): number {
    const nameComparison = compareSortValues(left.productName, right.productName);
    return nameComparison !== 0 ? nameComparison : left.productId - right.productId;
  }
}
