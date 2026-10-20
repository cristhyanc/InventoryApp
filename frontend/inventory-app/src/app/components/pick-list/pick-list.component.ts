import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { EMPTY, catchError, tap } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { PickListService } from '../../services/pick-list.service';
import { ToastService } from '../../services/toast.service';
import { Machine, PickListMachineQuantity, PickListProduct, Product } from '../../models/models';
import { ListLoadState } from '../shared/list-load-state';
import { MultiSelectDropdownComponent, MultiSelectOption } from '../shared/multi-select-dropdown.component';
import { BusinessDateTimePipe } from '../../formatting/business-date-time.pipe';
import { PERMISSION_NOT_GRANTED_REASON, nayaxUnavailableMessage } from '../../provider/nayax-availability';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { loadProviderList } from '../../provider/provider-list-load';
import { NayaxStatusBannerComponent } from '../shared/nayax-status-banner/nayax-status-banner.component';
import { SortDirection, compareSortValues, nextSortState, sortAriaState, sortColumnLabel } from '../shared/sortable-column';
import { ManualPickListComponent } from './manual-pick-list.component';

type PickListSortColumn = 'name' | 'mdbCode';

/** Which of the page's two views is showing. See `PickListComponent.calculatedUnavailableReason`. */
export type PickListMode = 'calculated' | 'manual';

/**
 * Pick List (Restock Planning) page (issues #222, #226): a read-only matrix built on the #221
 * backend projection. Product and machine selection is staged in the Products/Machines dropdown
 * filters and only takes effect on "Apply"; picked/unpicked completion tracking is entirely
 * frontend, in-memory state - nothing here is persisted, and no mutation endpoint is ever called,
 * so refreshing the page always returns to an empty plan.
 *
 * **The whole page is a live fleet read**, so without a usable provider there is nothing to plan
 * (issue #728). The machine list goes through `loadProviderList`: a provider that cannot list
 * machines leaves {@link machinesUnavailable} - one sentence in place of the dropdowns and the
 * matrix, because "No machines selected yet" is a claim about this business's selection rather than
 * about Nayax - and a machine read that *failed* leaves {@link machinesError}, which says so instead
 * of presenting an empty dropdown as "this business has no machines".
 *
 * **Issue #744 adds a second, machine-free view: manual entry.** With a usable provider and the
 * permissions the calculated list needs (the machine fleet read and each machine's own product
 * read), both views are offered and {@link mode} defaults to `'calculated'`, unchanged from before
 * this issue. Without either - {@link calculatedUnavailableReason} is non-empty, from
 * {@link machinesUnavailable} or from a stated refusal of the `machineStock` permission, checked
 * up front and, for the latter, also caught live if a selected machine's own product read is
 * refused only once Apply actually asks for it - the page opens straight into manual mode and
 * states why, and the toggle to switch back to calculated is not offered at all: there is nothing
 * to switch to. `ManualPickListComponent` is composed in rather than grown inline (docs/
 * architecture.md § Page composition boundary) and is handed {@link allProducts}, the same
 * already-fetched active catalogue the calculated view's Products filter uses, rather than fetching
 * it a second time.
 *
 * **A mode switch never destroys the manual entry, because manual entry can hold an unconfirmed
 * write.** Manual mode is the only view here that saves anything, and what protects it from
 * recording a pick twice is the exact row set and idempotency key its component keeps for a retry
 * (see `ManualPickListComponent`). Destroying it would throw both away, so an operator whose save
 * ended uncertain could switch views, retype the same pick, and deduct the same storage a second
 * time under a new key. So the component is created once, the first time manual mode is shown
 * ({@link manualViewCreated}), and is **hidden rather than destroyed** afterwards - the same
 * treatment `MachineDetailComponent` gives its tab panels - and while a save is actually in flight
 * ({@link manualSaveInFlight}) switching away is refused outright, so no response can land on a view
 * the operator has already left. The calculated matrix is a read-only projection, so it has nothing
 * comparable to lose; its own state survives because nothing clears it.
 */
@Component({
  selector: 'app-pick-list',
  standalone: true,
  imports: [CommonModule, MultiSelectDropdownComponent, BusinessDateTimePipe, NayaxStatusBannerComponent, ManualPickListComponent],
  templateUrl: './pick-list.component.html'
})
export class PickListComponent implements OnInit {
  allMachines: Machine[] = [];
  allProducts: Product[] = [];

  /** Which view is showing; see the class doc for when it is forced to `'manual'`. */
  mode: PickListMode = 'calculated';

  /**
   * Whether `ManualPickListComponent` exists yet. It is created the first time manual mode is shown
   * and then kept alive - hidden while the calculated view is showing - so its typed quantities and
   * its retry key survive every later switch. A page that never enters manual mode never builds it.
   */
  manualViewCreated = false;

  /** Whether manual entry has a save outstanding; while it has, switching away from manual is refused. */
  manualSaveInFlight = false;

  /** The one sentence saying Nayax has refused the `machineStock` permission the calculated list needs; `''` while it has not. */
  machineProductsUnavailable = '';

  /** Staged filter choices from the dropdowns, not yet applied to the matrix. */
  stagedMachineIds: number[] = [];
  stagedProductIds: number[] = [];

  /** The last-applied filter choices: what the matrix columns/rows and `pickListProducts` reflect. */
  appliedMachineIds: number[] = [];
  appliedProductIds: number[] = [];

  /** The last-fetched read-only projection for `appliedMachineIds`. */
  pickListProducts: PickListProduct[] = [];

  /** Ephemeral picked/unpicked state, keyed by `productId:machineId`. Never persisted. */
  private readonly pickedCells = new Set<string>();

  /** Client-side only: until a header is clicked, rows stay in the backend's default order. */
  sortColumn: PickListSortColumn | null = null;
  sortDirection: SortDirection = 'asc';

  lastRefreshedAt: Date | null = null;
  readonly loadState = new ListLoadState();

  /** The one sentence for a provider that cannot list machines; `''` while it can. */
  machinesUnavailable = '';

  /** Set when the machine list could not be read for a reason that is not an availability answer. */
  machinesError = '';

  constructor(
    private machineService: MachineService,
    private productService: ProductService,
    private pickListService: PickListService,
    private toastService: ToastService,
    private provider: ProviderAvailabilityService
  ) {}

  ngOnInit(): void {
    loadProviderList(this.provider, 'machineFleet', () => this.machineService.getAll()).subscribe((outcome) => {
      this.allMachines = outcome.kind === 'listed' ? outcome.items : [];
      this.machinesUnavailable =
        outcome.kind === 'unavailable' ? nayaxUnavailableMessage(outcome.unavailability, 'The pick list') : '';
      this.machinesError =
        outcome.kind === 'failed' ? 'The machine list could not be loaded, so a pick list cannot be built.' : '';
      if (this.machinesUnavailable !== '') {
        this.setMode('manual');
      }
    });
    // Checked up front, beside the machine fleet above: a usable connection does not mean Nayax has
    // granted every read the calculated list needs, and a permission this session already knows is
    // refused must open straight into manual mode rather than offering a matrix that will only fail.
    this.provider.featureAvailability('machineStock').subscribe((availability) => {
      if (availability === 'permissionNotGranted') {
        this.machineProductsUnavailable = nayaxUnavailableMessage(PERMISSION_NOT_GRANTED_REASON, 'The calculated pick list');
        this.setMode('manual');
      }
    });
    this.productService.getAll().subscribe((products) => {
      this.allProducts = products;
      const allProductIds = products.map((p) => p.id);
      this.stagedProductIds = allProductIds;
      this.appliedProductIds = allProductIds;
    });
  }

  /**
   * The one sentence saying why the calculated view is not a real choice right now, or `''` while
   * it is. Non-empty forces {@link mode} to `'manual'` and hides the toggle to switch back: there
   * is nothing to switch to.
   */
  get calculatedUnavailableReason(): string {
    return this.machinesUnavailable || this.machineProductsUnavailable;
  }

  /**
   * Switches the visible view. Switching to `'calculated'` is refused once
   * {@link calculatedUnavailableReason} is set (there is nothing to switch to) and while
   * {@link manualSaveInFlight} is set (an outstanding save must land where the operator can see
   * what it did); the toggle button is disabled in both cases, and this is the same rule behind it.
   */
  switchMode(mode: PickListMode): void {
    if (mode === 'calculated' && (this.calculatedUnavailableReason !== '' || this.manualSaveInFlight)) {
      return;
    }
    this.setMode(mode);
  }

  /** Refreshes the active catalogue `ManualPickListComponent` reads, after it records a refill. */
  refreshProductsAfterManualRefill(): void {
    this.productService.getAll().subscribe((products) => (this.allProducts = products));
  }

  /** Records whether manual entry has a save outstanding; see {@link switchMode}. */
  onManualSaveInFlightChange(inFlight: boolean): void {
    this.manualSaveInFlight = inFlight;
  }

  /** The one place `mode` is written, so entering manual mode always creates the view that must then survive. */
  private setMode(mode: PickListMode): void {
    this.mode = mode;
    if (mode === 'manual') {
      this.manualViewCreated = true;
    }
  }

  /**
   * Whether the machine selection and the matrix are offered at all. They are not when there is no
   * machine list to plan against: the page then states why, once, instead of offering a selection
   * that can never be made.
   */
  get machineSelectionShown(): boolean {
    return this.machinesUnavailable === '' && this.machinesError === '';
  }

  machineOptions(): MultiSelectOption[] {
    return this.allMachines.map((m) => ({ id: m.machineID, label: this.machineLabel(m.machineID) }));
  }

  productOptions(): MultiSelectOption[] {
    return this.allProducts.map((p) => ({ id: p.id, label: p.name }));
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

  /** Applies the staged Products/Machines selections together; only a changed machine selection refetches. */
  applyFilters(): void {
    const machineSelectionChanged = !this.sameIds(this.stagedMachineIds, this.appliedMachineIds);
    this.appliedProductIds = [...this.stagedProductIds];
    this.appliedMachineIds = [...this.stagedMachineIds];

    if (machineSelectionChanged) {
      this.fetchOrClear(this.appliedMachineIds);
    }
  }

  /** Removes one machine from both the applied and staged selections and refreshes/clears the matrix. */
  removeMachine(machineId: number): void {
    this.appliedMachineIds = this.appliedMachineIds.filter((id) => id !== machineId);
    this.stagedMachineIds = this.stagedMachineIds.filter((id) => id !== machineId);
    for (const key of [...this.pickedCells]) {
      if (this.keyMachineId(key) === machineId) {
        this.pickedCells.delete(key);
      }
    }
    this.fetchOrClear(this.appliedMachineIds);
  }

  resetAll(): void {
    const allProductIds = this.allProducts.map((p) => p.id);
    this.stagedProductIds = allProductIds;
    this.appliedProductIds = allProductIds;
    this.stagedMachineIds = [];
    this.appliedMachineIds = [];
    this.pickListProducts = [];
    this.pickedCells.clear();
    this.lastRefreshedAt = null;
    this.sortColumn = null;
    this.sortDirection = 'asc';
  }

  visibleProducts(): PickListProduct[] {
    const filtered = this.pickListProducts.filter((p) => this.appliedProductIds.includes(p.productId));
    return this.sortColumn === null ? filtered : this.sortProducts(filtered);
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

  isPicked(productId: number, machineId: number): boolean {
    return this.pickedCells.has(this.cellKey(productId, machineId));
  }

  /** Toggles picked/unpicked. Only a positive-pick cell is actionable; a zero-pick cell never toggles. */
  togglePicked(product: PickListProduct, machineId: number): void {
    const cell = this.cellFor(product, machineId);
    if (!cell || cell.quantityToPick <= 0) {
      return;
    }
    const key = this.cellKey(product.productId, machineId);
    if (this.pickedCells.has(key)) {
      this.pickedCells.delete(key);
    } else {
      this.pickedCells.add(key);
    }
  }

  totalToPickUnits(): number {
    return this.visibleProducts().reduce((sum, p) => sum + p.totalQuantityToPick, 0);
  }

  actionableCellCount(): number {
    return this.actionableCells().length;
  }

  pickedActionableCellCount(): number {
    return this.actionableCells().filter((c) => this.isPicked(c.productId, c.machineId)).length;
  }

  progressPercent(): number {
    const total = this.actionableCellCount();
    return total === 0 ? 0 : Math.round((this.pickedActionableCellCount() / total) * 100);
  }

  shortageProducts(): PickListProduct[] {
    return this.visibleProducts().filter((p) => p.storageShortageQuantity > 0);
  }

  /** Re-fetches the live Nayax-backed projection for the given machine ids, or clears the matrix if none are selected. */
  private fetchOrClear(machineIds: number[]): void {
    if (machineIds.length === 0) {
      this.pickListProducts = [];
      this.pickedCells.clear();
      this.lastRefreshedAt = null;
      return;
    }

    const token = this.loadState.start();
    this.pickListService.get(machineIds).pipe(
      tap((result) => {
        if (this.loadState.isCurrent(token)) {
          this.pickListProducts = result.products;
          this.reconcilePickedCells();
          this.lastRefreshedAt = new Date();
        }
        this.loadState.succeed(token);
      }),
      catchError((error: unknown) => {
        this.loadState.fail(token);
        // The permission `machineStock` evidence above is read once at init; a session's first
        // concrete proof that it is refused is often this very read, once Apply actually asks a
        // machine for its products, so it is recorded here too rather than only reported as a
        // generic failure.
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

  private actionableCells(): { productId: number; machineId: number }[] {
    const cells: { productId: number; machineId: number }[] = [];
    for (const product of this.visibleProducts()) {
      for (const mq of product.machineQuantities) {
        if (mq.quantityToPick > 0 && this.appliedMachineIds.includes(mq.machineId)) {
          cells.push({ productId: product.productId, machineId: mq.machineId });
        }
      }
    }
    return cells;
  }

  /** Drops any picked mark whose cell no longer exists or is no longer actionable after a data refresh. */
  private reconcilePickedCells(): void {
    const stillActionable = new Set(
      this.pickListProducts.flatMap((p) =>
        p.machineQuantities.filter((mq) => mq.quantityToPick > 0).map((mq) => this.cellKey(p.productId, mq.machineId))
      )
    );
    for (const key of [...this.pickedCells]) {
      if (!stillActionable.has(key)) {
        this.pickedCells.delete(key);
      }
    }
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

  private sameIds(a: number[], b: number[]): boolean {
    if (a.length !== b.length) {
      return false;
    }
    const bSet = new Set(b);
    return a.every((id) => bSet.has(id));
  }

  private cellKey(productId: number, machineId: number): string {
    return `${productId}:${machineId}`;
  }

  private keyMachineId(key: string): number {
    return Number(key.split(':')[1]);
  }
}
