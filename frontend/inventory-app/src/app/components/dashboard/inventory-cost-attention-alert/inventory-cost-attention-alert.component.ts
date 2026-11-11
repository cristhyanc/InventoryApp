import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subscription } from 'rxjs';
import {
  InventoryCostAttentionService,
  ProductNeedingCostAttention
} from '../../../services/inventory-cost-attention.service';
import { IconComponent } from '../../shared/icon.component';

/**
 * The Dashboard's "costs need attention" alert: one alert in the Needs attention panel, shown while
 * any product's inventory-cost recalculation has failed and is still waiting to be retried.
 *
 * It replaces what used to be a business-wide "Latest Nayax sales could not be synchronized"
 * warning: one product with a gap in its stock history used to fail the whole sales sync. The sales
 * now sync regardless, and the product that needs fixing is named here instead.
 *
 * A failed read shows nothing rather than a false "nothing needs attention".
 */
@Component({
  selector: 'app-inventory-cost-attention-alert',
  standalone: true,
  imports: [CommonModule, IconComponent],
  host: { class: 'contents' },
  template: `
    @if (isShown) {
      <output class="flex items-start gap-3 rounded-lg border border-rr-line bg-white p-3 font-rr" data-testid="inventory-cost-attention-alert">
        <span class="mt-0.5 shrink-0 text-rr-warn-text" aria-hidden="true"><app-icon name="inventory_2" [size]="20" /></span>
        <span class="block min-w-0">
          <span class="block text-sm font-semibold text-rr-navy" data-testid="inventory-cost-attention-alert-title">{{ title }}</span>
          <span class="block text-sm text-rr-muted" data-testid="inventory-cost-attention-alert-detail">{{ detail }}</span>
        </span>
      </output>
    }
  `
})
export class InventoryCostAttentionAlertComponent implements OnInit, OnDestroy {
  products: ProductNeedingCostAttention[] = [];

  private subscription: Subscription | null = null;

  constructor(private readonly attention: InventoryCostAttentionService) {}

  ngOnInit(): void {
    this.subscription = this.attention.list().subscribe({
      next: (products) => (this.products = products),
      error: () => (this.products = [])
    });
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
  }

  /** Whether any product's cost recalculation is waiting on a fix. */
  get isShown(): boolean {
    return this.products.length > 0;
  }

  /**
   * Names the products and says what usually fixes them. The sales themselves are synchronized and
   * counted; only these products' stock costs are waiting.
   */
  get detail(): string {
    const names = this.products.map((product) => product.productName ?? `Product ${product.productId}`).join(', ');
    return `${names}: a sale could not be costed, usually because no purchase is recorded before it. Record the missing purchase, or add a costing repair, dated before that sale. Nayax sales are still being synchronized.`;
  }

  /** For example "Costs for 1 product could not be recalculated". */
  get title(): string {
    const count = this.products.length;
    return count === 1
      ? 'Costs for 1 product could not be recalculated'
      : `Costs for ${count} products could not be recalculated`;
  }
}
