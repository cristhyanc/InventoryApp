import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Product } from '../../../models/models';
import { ProductService } from '../../../services/product.service';
import { CostingRepairComponent } from './costing-repair.component';

/**
 * Dedicated page for the Costing Repair workflow, split out of `AdminComponent` (issue #390,
 * Admin split 3/3). The workflow itself stays exactly where issue #361 put it: the standalone
 * `CostingRepairComponent` owns the whole preview/apply/history form, state, API calls and
 * notifications, and this page only loads the product list it needs and composes it through
 * `[products]`, per docs/architecture.md § Page composition boundary. No repair validation,
 * idempotency or costing-rebuild rule exists on this page.
 */
@Component({
  selector: 'app-costing-repair-page',
  standalone: true,
  imports: [CommonModule, RouterLink, CostingRepairComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <h1 class="page-title mt-2">Costing Repair</h1>
          <p class="page-subtitle">An applied repair changes historical cost of goods sold and cannot be reversed. Preview and confirm before applying.</p>
        </div>
      </header>

      <app-costing-repair [products]="products"></app-costing-repair>
    </div>
  `
})
export class CostingRepairPageComponent {
  products: Product[] = [];

  constructor(private readonly productService: ProductService) {
    this.productService.getAll().subscribe({ next: products => this.products = products, error: () => this.products = [] });
  }
}
