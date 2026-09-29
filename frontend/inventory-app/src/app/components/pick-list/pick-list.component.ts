import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { EMPTY, catchError, tap } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ProductService } from '../../services/product.service';
import { PickListService } from '../../services/pick-list.service';
import { ToastService } from '../../services/toast.service';
import { Machine, PickListMachineQuantity, PickListProduct, Product } from '../../models/models';
import { ListLoadState } from '../shared/list-load-state';

/**
 * Pick List (Restock Planning) page (issue #222): a read-only matrix built on the #221 backend
 * projection. Machine selection, product filtering and picked/unpicked completion tracking are
 * entirely frontend, in-memory state - nothing here is persisted, and no mutation endpoint is ever
 * called, so refreshing the page always returns to an empty plan.
 */
@Component({
  selector: 'app-pick-list',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './pick-list.component.html'
})
export class PickListComponent implements OnInit {
  allMachines: Machine[] = [];
  allProducts: Product[] = [];

  /** Ephemeral: which machines are currently selected as columns. Never persisted. */
  selectedMachineIds: number[] = [];

  /** Ephemeral: which products the matrix rows are restricted to. Empty means "show all". */
  selectedProductIds: number[] = [];

  /** The last-fetched read-only projection for `selectedMachineIds`. */
  pickListProducts: PickListProduct[] = [];

  /** Ephemeral picked/unpicked state, keyed by `productId:machineId`. Never persisted. */
  private readonly pickedCells = new Set<string>();

  machineToAdd: number | '' = '';
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
    this.productService.getAll().subscribe((products) => (this.allProducts = products));
  }

  availableMachinesToAdd(): Machine[] {
    return this.allMachines.filter((m) => !this.selectedMachineIds.includes(m.machineID));
  }

  machineLabel(machineId: number): string {
    const machine = this.allMachines.find((m) => m.machineID === machineId);
    if (!machine) {
      return `Machine #${machineId}`;
    }
    return machine.machineName || machine.machineNumber || `Machine #${machineId}`;
  }

  addMachine(): void {
    if (this.machineToAdd === '') {
      return;
    }
    const machineId = Number(this.machineToAdd);
    this.machineToAdd = '';
    if (this.selectedMachineIds.includes(machineId)) {
      return;
    }
    this.selectedMachineIds = [...this.selectedMachineIds, machineId];
    this.refresh();
  }

  removeMachine(machineId: number): void {
    this.selectedMachineIds = this.selectedMachineIds.filter((id) => id !== machineId);
    for (const key of [...this.pickedCells]) {
      if (this.keyMachineId(key) === machineId) {
        this.pickedCells.delete(key);
      }
    }
    this.refresh();
  }

  onProductFilterChange(event: Event): void {
    const options = (event.target as HTMLSelectElement).selectedOptions;
    this.selectedProductIds = Array.from(options).map((option) => Number(option.value));
  }

  /** Re-fetches the live Nayax-backed projection ("Apply"/refresh) for the currently selected machines. */
  refresh(): void {
    if (this.selectedMachineIds.length === 0) {
      this.pickListProducts = [];
      this.pickedCells.clear();
      this.lastRefreshedAt = null;
      return;
    }

    const token = this.loadState.start();
    this.pickListService.get(this.selectedMachineIds).pipe(
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

  resetAll(): void {
    this.selectedMachineIds = [];
    this.selectedProductIds = [];
    this.machineToAdd = '';
    this.pickListProducts = [];
    this.pickedCells.clear();
    this.lastRefreshedAt = null;
  }

  visibleProducts(): PickListProduct[] {
    if (this.selectedProductIds.length === 0) {
      return this.pickListProducts;
    }
    return this.pickListProducts.filter((p) => this.selectedProductIds.includes(p.productId));
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

  private actionableCells(): { productId: number; machineId: number }[] {
    const cells: { productId: number; machineId: number }[] = [];
    for (const product of this.visibleProducts()) {
      for (const mq of product.machineQuantities) {
        if (mq.quantityToPick > 0 && this.selectedMachineIds.includes(mq.machineId)) {
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

  private cellKey(productId: number, machineId: number): string {
    return `${productId}:${machineId}`;
  }

  private keyMachineId(key: string): number {
    return Number(key.split(':')[1]);
  }
}
