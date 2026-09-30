import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ProductService } from '../../services/product.service';
import { InventoryCountService } from '../../services/inventory-count.service';
import { ToastService } from '../../services/toast.service';
import { InventoryCountApplyOutcome, Product } from '../../models/models';
import { ListLoadState } from '../shared/list-load-state';

/** One product's counting-session state on the Take Inventory page. Entirely client-side except for a successful Apply. */
export interface TakeInventoryRow {
  product: Product;
  countedStock: number | null;
  /** Row is visibly complete for this session: either confirmed unchanged (no backend call) or successfully applied. */
  complete: boolean;
  applying: boolean;
  error: string | null;
}

/**
 * Take Inventory (issue #245): a compact table for counting physical storage stock across products.
 * Confirming an unchanged Current Stock is frontend/session state only - it never calls the backend.
 * Apply always calls the backend, even when Counted equals Current, so a zero difference is
 * confirmed the same way through either path; the backend is the sole authority over whether a
 * non-zero difference is safe to apply against the authoritative current quantity.
 */
@Component({
  selector: 'app-take-inventory',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './take-inventory.component.html'
})
export class TakeInventoryComponent implements OnInit {
  rows: TakeInventoryRow[] = [];
  readonly loadState = new ListLoadState();

  constructor(
    private productService: ProductService,
    private inventoryCountService: InventoryCountService,
    private toastService: ToastService
  ) {}

  ngOnInit(): void {
    const token = this.loadState.start();
    this.productService.getAll().subscribe({
      next: (products) => {
        if (!this.loadState.isCurrent(token)) return;
        this.rows = products.map((product) => this.rowFor(product));
        this.loadState.succeed(token);
      },
      error: () => {
        if (!this.loadState.isCurrent(token)) return;
        this.loadState.fail(token);
      }
    });
  }

  private rowFor(product: Product): TakeInventoryRow {
    return { product, countedStock: null, complete: false, applying: false, error: null };
  }

  /** Counted minus Current, or null until a counted stock has been entered. */
  difference(row: TakeInventoryRow): number | null {
    return row.countedStock === null ? null : row.countedStock - row.product.quantityInStock;
  }

  /**
   * Confirms an unchanged physical count: marks the row green/complete without a stock mutation
   * or persistence call, matching Counted Stock to the currently displayed Current Stock so the
   * difference reads as zero.
   */
  confirmCurrentStock(row: TakeInventoryRow): void {
    row.countedStock = row.product.quantityInStock;
    row.complete = true;
    row.error = null;
  }

  /** Editing Counted Stock after a row was confirmed/applied reopens it for a fresh count. */
  onCountedStockChange(row: TakeInventoryRow): void {
    row.complete = false;
    row.error = null;
  }

  apply(row: TakeInventoryRow): void {
    if (row.countedStock === null) {
      row.error = 'Enter a counted quantity.';
      return;
    }
    if (row.countedStock < 0) {
      row.error = 'Counted stock cannot be negative.';
      return;
    }

    row.error = null;
    row.applying = true;
    const countedStock = row.countedStock;
    const expectedCurrentStock = row.product.quantityInStock;

    this.inventoryCountService.apply(row.product.id, { countedStock, expectedCurrentStock }).subscribe({
      next: (result) => {
        row.applying = false;
        row.product.quantityInStock = result.currentStock;
        row.countedStock = result.currentStock;
        row.complete = true;
        row.error = null;
        if (result.outcome === InventoryCountApplyOutcome.Applied) {
          this.toastService.success(`Applied a ${result.quantityChange > 0 ? '+' : ''}${result.quantityChange} count adjustment for ${row.product.name}.`);
        } else {
          this.toastService.success(`${row.product.name} confirmed: no count difference.`);
        }
      },
      error: (err) => {
        row.applying = false;
        row.complete = false;
        row.error = this.errorMessage(err);
        this.toastService.error(row.error);
        this.refreshCurrentStock(row);
      }
    });
  }

  /**
   * Re-reads the product after a failed Apply (a stale-count conflict or a validation failure) so
   * the displayed Current Stock never keeps showing state the backend has already rejected.
   */
  private refreshCurrentStock(row: TakeInventoryRow): void {
    this.productService.get(row.product.id).subscribe({
      next: (product) => (row.product = product)
    });
  }

  private errorMessage(err: unknown): string {
    const body = (err as { error?: { message?: string; title?: string } } | undefined)?.error;
    return (typeof body === 'string' ? body : body?.message ?? body?.title) ?? 'Failed to apply the inventory count.';
  }
}
