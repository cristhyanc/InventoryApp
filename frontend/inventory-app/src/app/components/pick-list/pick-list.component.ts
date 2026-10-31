import { Component, HostListener, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { EMPTY, Subscription, catchError, tap } from 'rxjs';
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
  InvalidPickQuantity,
  MAXIMUM_PICK_QUANTITY,
  parsePickQuantity,
  picksCompletedLabel,
  planCellKey,
  planCellMachineId,
  storageShortage
} from './plan-picks';
import { PlanPicksRestoration, PlanPicksStoreService, UNRESOLVED_SCOPE_TOKEN } from './plan-picks-store.service';
import { StoredPlanMachine, StoredPlanPicks } from './plan-picks-storage';
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

/** One run of matrix columns under a shared site header; `siteId` is the grouping key, `siteName` only its label. */
export interface ColumnGroup {
  siteId: number | null;
  siteName: string | null;
  machineIds: number[];
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
 * endpoint: an operator may **adjust a suggested pick quantity** - an override kept beside the
 * original suggestion, never a change to stock or to a target - and **check a pick off**.
 *
 * **Since issue #1008 every editable cell carries its own always-visible box between a minus and a
 * plus button**, with the completion checkbox still at its right: there is no edit mode, no pencil
 * and no confirm step. Each keystroke that reads as a nonnegative whole number the pick list can
 * represent is the new override at once; text that does not ({@link invalidQuantities}) is kept
 * exactly as typed as pending work, refused with an inline message, and leaves the cell's last
 * valid quantity carrying the totals while that cell can neither be stepped nor completed. A
 * changed effective quantity clears that cell's checkmark, and `0` is a deliberate
 * skip that no longer counts as a pick. Totals, storage shortages and progress are computed from
 * the effective quantities with the backend projection's own formulas (`total = Σ picks`,
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
 * **Plan picks survives leaving the page, because the plan is kept in this tab** (issue #1021).
 * Every change to the applied selection, the snapshot or the operator's own overrides, ticks and
 * pending text is written to `sessionStorage` through {@link PlanPicksStoreService}, and opening the
 * page again restores all of it - including the snapshot and its original "last refreshed" instant,
 * so coming back reads no new pick list and the overrides stay tied to the snapshot they were made
 * against. Display state (search, category, status filter, sort, phone machine focus) is not
 * restored. Every existing discard clears the stored copy too, and a stored plan is trimmed to the
 * machines the fleet read still lists.
 *
 * **A plan belongs to the account, business and context it was planned in, for its whole life.**
 * Each plan is bound to the scope token it was read or planned under ({@link planScopeToken}): that
 * token is what {@link PlanPicksStoreService.save} is given, so planning work is never written under
 * a scope that became current after it was made, and when
 * {@link PlanPicksStoreService.scopeToken$} reports any change - another signed-in account, another
 * business, a started support session or role preview, capabilities being read again - a pending
 * restoration is dropped and what is on screen is discarded with its stored copy
 * ({@link discardPlanForScopeChange}), rather than left displaying one business's figures to
 * another's context. The business **name** is never the proof of which business a plan belongs to:
 * a restored plan's machines must still match the machine records this business owns
 * (`StoredPlanMachine`), and a plan whose machines the fleet read cannot identify is not stored at
 * all - so the departure warning goes on protecting it.
 *
 * **Leaving the page is guarded** ({@link confirmLeave}, wired as the route's `canDeactivate`, and
 * {@link onBeforeUnload} for a reload or a closed tab): typed refill quantities, planning work this
 * tab could not store, and above all an unconfirmed save - whose retry context leaving destroys -
 * all ask first. Planning work stops asking only once the complete plan has actually been stored
 * ({@link unsavedPlanningWork}); a refused write keeps the warning, because the work really would
 * be lost. A cancelled departure changes nothing.
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

  /**
   * Planning overrides, keyed by {@link planCellKey}. A cell without one picks its suggestion.
   * Kept in this browser tab since issue #1021 ({@link PlanPicksStoreService}): stored beside the
   * snapshot it was made against, restored when the page opens again, and never sent anywhere.
   */
  private readonly overrides = new Map<string, number>();

  /** Checked-off picks, keyed by {@link planCellKey}. Kept in this tab exactly as {@link overrides} is. */
  private readonly pickedCells = new Set<string>();

  /**
   * The cells whose box holds text the plan cannot use, keyed by {@link planCellKey}. Work in
   * progress exactly as an override is: filtering, sorting, a mode switch and a changed machine
   * focus all leave it alone, only the confirmed discards clear it, and it is kept in this tab and
   * restored with the rest of the plan.
   */
  private readonly invalidQuantities = new Map<string, InvalidPickQuantity>();

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
   * Whether the complete plan now on screen is in this tab's storage (issue #1021). Only a
   * successful write sets it, which is what makes {@link leaveWarning} safe: a refused write leaves
   * it `false` and the departure warning keeps asking, because the work really would be lost.
   */
  private planPersisted = false;

  /**
   * The scope the plan on screen belongs to (issue #1021), and `null` while there is no plan to own.
   * It is captured when the snapshot is asked for or restored - never when the plan is written - so
   * a scope that changed in between refuses the write instead of inheriting somebody else's work.
   */
  private planScopeToken: number | null = null;

  /** The scope in effect, which a plan read or planned now would belong to. */
  private currentScopeToken = UNRESOLVED_SCOPE_TOKEN;

  /** Watches the scope for the lifetime of the page; see {@link discardPlanForScopeChange}. */
  private scopeWatch?: Subscription;

  /**
   * The stored plan read on arrival, held until the fleet read says which of its machines still
   * exist. `null` once it has been applied, dropped or found absent.
   */
  private pendingRestoration: PlanPicksRestoration | null = null;

  /** Whether the stored-plan read has answered, either way. */
  private restorationSettled = false;

  /** Whether the machine list has settled, which is what a stored plan is trimmed against. */
  private machinesSettled = false;

  /** Whether the one restore attempt this page makes is over, however it ended. */
  private restorationDone = false;

  /**
   * The stored-plan read, dropped when the page is destroyed: it waits for the scope to be known,
   * which a page left before anybody signed in would never see.
   */
  private restoration?: Subscription;

  /**
   * Whether a restored plan decided the Products selection, so the first catalogue read must not
   * seed it with every product and undo that.
   */
  private productSelectionRestored = false;

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
    private readonly siteService: SiteService,
    private readonly planStore: PlanPicksStoreService
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

    // Watched before anything is read or restored, so no plan can exist that is not bound to a
    // scope, and a scope that changes afterwards is acted on at once (issue #1021).
    this.scopeWatch = this.planStore.scopeToken$.subscribe((token) => this.onScopeToken(token));

    // Asked for before the fleet read below, and applied only once that read has settled: a stored
    // plan is restored against the machines the business still has (issue #1021).
    this.restoration = this.planStore.restore().subscribe((restoration) => {
      this.pendingRestoration = restoration;
      this.restorationSettled = true;
      this.restorePlanWhenReady();
    });

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
      this.machinesSettled = true;
      this.restorePlanWhenReady();
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
    this.restoration?.unsubscribe();
    this.scopeWatch?.unsubscribe();
  }

  /**
   * Reads the active catalogue: on arrival, after each recorded refill, and whenever the operator
   * retries a failed read. Only the first successful read seeds the Products filter's selection,
   * and not even that one when a restored plan has already chosen it.
   */
  loadProducts(): void {
    const token = this.productsLoadState.start();
    this.productService.getAll().subscribe({
      next: (products) => {
        if (this.productsLoadState.isCurrent(token)) {
          this.allProducts = products;
          // A restored plan already decided the Products selection (issue #1021), and seeding it
          // with every product would quietly widen the run the operator came back to.
          if (!this.productsEverLoaded && !this.productSelectionRestored) {
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
    } else {
      // A product-only change keeps the loaded snapshot, so the stored plan only has to record the
      // narrower run the operator would come back to.
      this.persistPlan();
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

  /** The quantity the plan picks for a cell: its override, else the suggestion. */
  plannedQuantity(productId: number, cell: PickListMachineQuantity): number {
    return this.overrides.get(planCellKey(productId, cell.machineId)) ?? cell.quantityToPick;
  }

  /** Whether a cell's effective quantity differs from its suggestion. */
  isOverridden(productId: number, machineId: number): boolean {
    return this.overrides.has(planCellKey(productId, machineId));
  }

  isPicked(productId: number, machineId: number): boolean {
    return this.pickedCells.has(planCellKey(productId, machineId));
  }

  /**
   * Toggles a pick's checkmark. Only a suggested cell whose effective quantity is above zero and
   * whose box the plan can read is a pick; a zero-suggestion cell, a skip and a cell holding text
   * the plan cannot use never toggle, so an unreadable cell can never be left looking complete.
   */
  togglePicked(product: PickListProduct, machineId: number): void {
    const cell = this.cellFor(product, machineId);
    if (!this.isSuggestedPick(cell) || !this.canComplete(product.productId, cell)) {
      return;
    }
    const key = planCellKey(product.productId, machineId);
    if (this.pickedCells.has(key)) {
      this.pickedCells.delete(key);
    } else {
      this.pickedCells.add(key);
    }
    this.persistPlan();
  }

  /** Whether a cell may be checked off right now: it plans a positive pick and its box is readable. */
  canComplete(productId: number, cell: PickListMachineQuantity): boolean {
    return !this.hasInvalidQuantity(productId, cell.machineId) && this.plannedQuantity(productId, cell) > 0;
  }

  /** The text one cell's box shows: its pending unusable text, else its effective quantity. */
  quantityInputValue(productId: number, cell: PickListMachineQuantity): string {
    return this.invalidQuantities.get(planCellKey(productId, cell.machineId))?.text ?? String(this.plannedQuantity(productId, cell));
  }

  /** The message refusing a cell's pending text, or `null` while its box holds a usable quantity. */
  quantityError(productId: number, machineId: number): string | null {
    return this.invalidQuantities.get(planCellKey(productId, machineId))?.error ?? null;
  }

  hasInvalidQuantity(productId: number, machineId: number): boolean {
    return this.invalidQuantities.has(planCellKey(productId, machineId));
  }

  /**
   * Takes every keystroke in a cell's box. The complete text is validated rather than truncated or
   * coerced: a nonnegative whole number the pick list can represent becomes the override
   * immediately - no confirm click, no blur - and anything else, a blank box included, is held as
   * typed with its message. Either way text that differs from the cell's last valid quantity
   * clears its checkmark, because it is no longer the pick that was checked off.
   */
  onQuantityInput(product: PickListProduct, machineId: number, raw: string): void {
    const cell = this.cellFor(product, machineId);
    if (!this.isSuggestedPick(cell)) {
      return;
    }
    const key = planCellKey(product.productId, machineId);
    const parsed = parsePickQuantity(raw);
    if ('error' in parsed) {
      this.invalidQuantities.set(key, { text: raw, error: parsed.error });
      this.pickedCells.delete(key);
      this.persistPlan();
      return;
    }
    this.invalidQuantities.delete(key);
    this.setPlannedQuantity(product.productId, cell, parsed.quantity);
  }

  /**
   * Steps a cell's quantity by exactly one from its effective quantity, within zero and the largest
   * quantity the pick list can represent. A cell whose box the plan cannot read is left for the
   * operator to correct, which is also why both of its buttons are disabled.
   */
  stepQuantity(product: PickListProduct, machineId: number, delta: number): void {
    const cell = this.cellFor(product, machineId);
    if (!this.isSuggestedPick(cell) || this.hasInvalidQuantity(product.productId, machineId)) {
      return;
    }
    const stepped = this.plannedQuantity(product.productId, cell) + delta;
    if (stepped < 0 || stepped > MAXIMUM_PICK_QUANTITY) {
      return;
    }
    this.setPlannedQuantity(product.productId, cell, stepped);
  }

  canDecreaseQuantity(productId: number, cell: PickListMachineQuantity): boolean {
    return !this.hasInvalidQuantity(productId, cell.machineId) && this.plannedQuantity(productId, cell) > 0;
  }

  canIncreaseQuantity(productId: number, cell: PickListMachineQuantity): boolean {
    return !this.hasInvalidQuantity(productId, cell.machineId) && this.plannedQuantity(productId, cell) < MAXIMUM_PICK_QUANTITY;
  }

  /** Puts a cell back on its suggestion, discarding any pending text its box holds. */
  useSuggestion(product: PickListProduct, machineId: number): void {
    const cell = this.cellFor(product, machineId);
    if (this.isSuggestedPick(cell)) {
      this.invalidQuantities.delete(planCellKey(product.productId, machineId));
      this.setPlannedQuantity(product.productId, cell, cell.quantityToPick);
    }
  }

  quantityInputId(productId: number, machineId: number): string {
    return `plan-pick-qty-${productId}-${machineId}`;
  }

  quantityErrorId(productId: number, machineId: number): string {
    return `plan-pick-qty-error-${productId}-${machineId}`;
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

  /** The whole applied run's picks: suggested cells whose effective quantity is above zero. A skip is not a pick. */
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
   * Whether the plan holds anything an operator would mind losing: an adjusted quantity, a checked
   * pick, or a box still holding text the plan cannot use, which is work in progress exactly as the
   * other two are.
   */
  hasPlanningWork(): boolean {
    return this.overrides.size > 0 || this.pickedCells.size > 0 || this.invalidQuantities.size > 0;
  }

  /**
   * Whether leaving would actually lose planning work: there is some, and this tab could not store
   * it (issue #1021). A plan that is stored is waiting on the page the operator comes back to, so
   * there is nothing to warn about; a refused write is not a stored plan, however much was
   * attempted, and goes on warning exactly as it did before.
   */
  private unsavedPlanningWork(): boolean {
    return this.hasPlanningWork() && !this.planPersisted;
  }

  /** The departure warning that applies right now, or `null` when leaving loses nothing. */
  leaveWarning(): string | null {
    if (this.manualSaveInFlight || this.manualView?.unresolvedSubmission) {
      return UNRESOLVED_SAVE_LEAVE_WARNING;
    }
    if (this.manualView?.hasEnteredQuantities() || this.unsavedPlanningWork()) {
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
    this.persistPlan();
  }

  /** Asks before removing machines that hold planning work; `true` when there is nothing to lose or the operator agreed. */
  private confirmMachineRemoval(machineIds: number[]): boolean {
    const withWork = machineIds.filter((id) => this.machineHasPlanningWork(id));
    return withWork.length === 0 || window.confirm(discardPlanMachineConfirmation(...withWork.map((id) => this.machineLabel(id))));
  }

  private machineHasPlanningWork(machineId: number): boolean {
    return [...this.overrides.keys(), ...this.pickedCells, ...this.invalidQuantities.keys()].some(
      (key) => planCellMachineId(key) === machineId
    );
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
    for (const key of this.invalidQuantities.keys()) {
      if (machineIds.includes(planCellMachineId(key))) {
        this.invalidQuantities.delete(key);
      }
    }
    if (this.appliedMachineIds.length === 0) {
      this.clearSnapshot();
    } else if (!this.loadState.loaded && !this.loadState.loadFailed) {
      this.fetchSnapshot();
    }
    this.updateRefillSuggestions();
    // The removed machines' keys are gone from the stored copy too, so coming back cannot bring
    // back a plan for a machine the operator dropped (issue #1021).
    this.persistPlan();
  }

  private discardPlanningWork(): void {
    this.overrides.clear();
    this.pickedCells.clear();
    this.invalidQuantities.clear();
    // Every discard is also a discard of the stored copy: a confirmed Refresh, Reset or
    // snapshot-replacing Apply must not be undone by coming back to the page (issue #1021).
    this.forgetPersistedPlan();
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
    // The plan this read will show belongs to the scope asking for it, not to whichever scope is in
    // effect when the answer comes back: a scope change meanwhile discards it instead (issue #1021).
    this.planScopeToken = this.currentScopeToken;
    const machineIds = [...this.appliedMachineIds];
    const token = this.loadState.start();
    const isLatest = (): boolean => generation === this.snapshotGeneration && this.loadState.isCurrent(token);
    this.pickListService.get(machineIds).pipe(
      tap((result) => {
        if (isLatest()) {
          this.pickListProducts = result.products;
          this.lastRefreshedAt = new Date();
          this.updateRefillSuggestions();
          this.persistPlan();
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

  /**
   * Takes every change of scope (issue #1021): another signed-in account, another business, a
   * started or ended support session or role preview, or capabilities being read again.
   *
   * Nothing planned under the previous scope may survive it. A restoration still waiting for the
   * fleet read is dropped unapplied, and a plan already on screen is discarded with its stored copy:
   * it holds the previous business's product names and storage figures, which the context now in
   * effect may not see, and it must not be written under the new scope either.
   */
  private onScopeToken(token: number): void {
    this.currentScopeToken = token;

    if (this.pendingRestoration !== null && this.pendingRestoration.scopeToken !== token) {
      this.pendingRestoration = null;
      // One restore attempt per visit, and this page's own scope no longer has one to make.
      this.restorationDone = true;
    }

    if (this.planScopeToken !== null && this.planScopeToken !== token) {
      this.discardPlanForScopeChange();
    }
  }

  /**
   * Drops the plan on screen because the scope that owns it is no longer the one in effect. It is
   * not a discard the operator asked for, so it asks nothing: there is no context left to ask in.
   * The stored copy goes with it, and the applied selection and the Products filter go back to where
   * a fresh visit starts, because both name this business's own machines and products.
   */
  private discardPlanForScopeChange(): void {
    this.stagedMachineIds = [];
    this.appliedMachineIds = [];
    this.focusedMachineId = null;
    const allProductIds = this.allProducts.map((p) => p.id);
    this.stagedProductIds = allProductIds;
    this.appliedProductIds = allProductIds;
    this.productSelectionRestored = false;
    // Bumps the snapshot generation too, so a read in flight for the previous scope cannot land.
    this.clearSnapshot();
  }

  /**
   * Applies the stored plan once both answers it depends on are in: the stored-plan read and the
   * fleet read it is trimmed against. Whatever happens, it is attempted once.
   */
  private restorePlanWhenReady(): void {
    if (this.restorationDone || !this.restorationSettled || !this.machinesSettled) {
      return;
    }

    this.restorationDone = true;
    const restoration = this.pendingRestoration;
    this.pendingRestoration = null;
    if (restoration === null) {
      return;
    }

    // The scope the plan was read for must still be the one in effect: the identity, the business
    // or the session context may have changed while the fleet read was outstanding, and a plan read
    // under the previous one belongs to neither the operator nor the business now in view.
    if (!this.planStore.isCurrentScope(restoration.scopeToken)) {
      return;
    }

    // The operator got there first - they applied a selection of their own while the reads were
    // outstanding - so the page they are using is not replaced by one they left earlier.
    if (this.appliedMachineIds.length > 0) {
      return;
    }

    this.applyRestoredPlan(restoration.plan, restoration.scopeToken);
  }

  /**
   * Puts a stored plan back on screen, trimmed to what still exists: machines the fleet read no
   * longer lists are dropped with their cells and their keys, and a plan with nothing left is
   * ignored and forgotten. Nothing here reads the pick list again, so the restored figures and
   * their "last refreshed" instant are exactly the ones the operator planned against; nothing here
   * touches {@link mode}, the preselected machine or Record refill either.
   *
   * **The fleet read is what proves the plan is this business's** (issue #1021). A record names the
   * machine record this business owned for each of its machines, and the fleet read names them now;
   * {@link verifiedRestoredMachineIds} refuses the whole record unless the two agree, because an
   * account whose membership moved to a different business of the same name matches the stored
   * account and business name perfectly well.
   */
  private applyRestoredPlan(plan: StoredPlanPicks, scopeToken: number): void {
    const machineIds = this.verifiedRestoredMachineIds(plan.appliedMachines);
    if (machineIds === null) {
      this.forgetPersistedPlan();
      return;
    }

    const products = plan.products
      .map((product) => ({
        ...product,
        machineQuantities: product.machineQuantities.filter((cell) => machineIds.includes(cell.machineId))
      }))
      .filter((product) => product.machineQuantities.length > 0);
    if (machineIds.length === 0 || products.length === 0) {
      this.forgetPersistedPlan();
      return;
    }

    const cellKeys = new Set(
      products.flatMap((product) => product.machineQuantities.map((cell) => planCellKey(product.productId, cell.machineId)))
    );

    this.planScopeToken = scopeToken;
    this.appliedMachineIds = machineIds;
    this.stagedMachineIds = [...machineIds];
    this.appliedProductIds = [...plan.appliedProductIds];
    this.stagedProductIds = [...plan.appliedProductIds];
    this.productSelectionRestored = true;
    this.pickListProducts = products;
    this.lastRefreshedAt = new Date(plan.lastRefreshedAt);

    for (const override of plan.overrides) {
      if (cellKeys.has(override.key)) {
        this.overrides.set(override.key, override.quantity);
      }
    }
    for (const key of plan.pickedCells) {
      if (cellKeys.has(key)) {
        this.pickedCells.add(key);
      }
    }
    for (const invalid of plan.invalidQuantities) {
      if (cellKeys.has(invalid.key)) {
        this.invalidQuantities.set(invalid.key, { text: invalid.text, error: invalid.error });
      }
    }

    // The matrix is showing a loaded snapshot, without a request having been made for it.
    this.loadState.succeed(this.loadState.start());
    this.updateRefillSuggestions();
    // What is on screen is the trimmed plan, so that is what the stored copy must now be - and
    // writing it is also what confirms the work is still safely stored for the next departure.
    this.persistPlan();
  }

  /**
   * The stored machines to restore, or `null` when the record may not be restored at all.
   *
   * A machine the fleet read no longer lists is dropped, as the issue requires. A machine it lists
   * under a **different** machine record of this business's own is not a changed machine: the same
   * provider number belongs to somebody else's fleet, which is proof the record was written for
   * another business, so the whole record is refused rather than trimmed to the part that happens to
   * agree. A record none of whose surviving machines carries a machine record at all is refused for
   * the opposite reason: there is nothing in it this page can check, and an unverifiable plan is not
   * restored on the strength of a business name alone.
   */
  private verifiedRestoredMachineIds(machines: readonly StoredPlanMachine[]): number[] | null {
    const kept: StoredPlanMachine[] = [];
    for (const machine of machines) {
      const listed = this.allMachines.find((m) => m.machineID === machine.machineId);
      if (listed === undefined) {
        continue;
      }
      if ((listed.localMachineId ?? null) !== machine.localMachineId) {
        return null;
      }
      kept.push(machine);
    }

    return kept.some((machine) => machine.localMachineId !== null) ? kept.map((machine) => machine.machineId) : null;
  }

  /**
   * Writes the complete plan to this tab, or forgets the stored copy when there is no loaded
   * snapshot left to come back to. {@link planPersisted} records the honest outcome, and a refused
   * write takes the previous copy with it rather than leaving an older plan as the newest one.
   *
   * **A plan is written only with the business identity that can prove whose it is**, and only for
   * the scope it was planned under: {@link storableMachines} answers `null` when no applied machine
   * carries one of this business's own machine records, and the write is refused if the scope moved
   * on. Either way nothing is stored and {@link planPersisted} stays false, so the existing
   * departure warning goes on protecting work this tab cannot safely keep.
   */
  private persistPlan(): void {
    const machines = this.storableMachines();
    if (machines === null || this.lastRefreshedAt === null || this.planScopeToken === null) {
      this.forgetPersistedPlan();
      return;
    }

    this.planPersisted = this.planStore.save(
      {
        appliedMachines: machines,
        appliedProductIds: [...this.appliedProductIds],
        products: this.pickListProducts,
        lastRefreshedAt: this.lastRefreshedAt.toISOString(),
        overrides: [...this.overrides].map(([key, quantity]) => ({ key, quantity })),
        pickedCells: [...this.pickedCells],
        invalidQuantities: [...this.invalidQuantities].map(([key, invalid]) => ({ key, ...invalid }))
      },
      this.planScopeToken
    );
    if (!this.planPersisted) {
      this.planStore.clear();
    }
  }

  /**
   * The applied machines as a stored plan names them - the provider number and this business's own
   * machine record for it - or `null` when there is no plan to store: nothing is applied, or not one
   * applied machine has a machine record, which would leave the plan with no business identity to
   * verify when it is read back.
   */
  private storableMachines(): StoredPlanMachine[] | null {
    if (this.appliedMachineIds.length === 0) {
      return null;
    }

    const machines = this.appliedMachineIds.map((machineId) => ({
      machineId,
      localMachineId: this.allMachines.find((m) => m.machineID === machineId)?.localMachineId ?? null
    }));
    return machines.some((machine) => machine.localMachineId !== null) ? machines : null;
  }

  private forgetPersistedPlan(): void {
    this.planStore.clear();
    this.planPersisted = false;
    this.planScopeToken = null;
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
