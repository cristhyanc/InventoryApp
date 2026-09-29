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

/**
 * Pick List (Restock Planning) page (issues #222, #226): a read-only matrix built on the #221
 * backend projection. Product and machine selection is staged in the Products/Machines dropdown
 * filters and only takes effect on "Apply"; picked/unpicked completion tracking is entirely
 * frontend, in-memory state - nothing here is persisted, and no mutation endpoint is ever called,
 * so refreshing the page always returns to an empty plan.
 */
@Component({
  selector: 'app-pick-list',
  standalone: true,
  imports: [CommonModule, MultiSelectDropdownComponent],
  templateUrl: './pick-list.component.html'
})
export class PickListComponent implements OnInit {
  allMachines: Machine[] = [];
  allProducts: Product[] = [];

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

  lastRefreshedAt: Date | null = null;
  readonly loadState = new ListLoadState();

  constructor(
    private machineService: MachineService,
    private productService: ProductService,
    private pickListService: PickListService,
    private toastService: ToastService
  ) {}

  ngOnInit(): void {
    this.machineService.getAll().subscribe((machines) => (this.allMachines = machines));
    this.productService.getAll().subscribe((products) => {
      this.allProducts = products;
      const allProductIds = products.map((p) => p.id);
      this.stagedProductIds = allProductIds;
      this.appliedProductIds = allProductIds;
    });
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
  }

  visibleProducts(): PickListProduct[] {
    return this.pickListProducts.filter((p) => this.appliedProductIds.includes(p.productId));
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
      catchError(() => {
        this.loadState.fail(token);
        this.toastService.error('Failed to load the pick list.');
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
