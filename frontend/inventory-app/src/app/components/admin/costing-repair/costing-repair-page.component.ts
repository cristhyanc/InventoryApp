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
    <div class="mb-6">
      <a routerLink="/admin" class="text-sm text-blue-600 hover:underline">&larr; Back to Admin</a>
      <h1 class="mt-2 text-2xl font-semibold text-slate-800">Costing Repair</h1>
      <p class="mt-1 text-sm text-slate-500">An applied repair changes historical cost of goods sold and cannot be reversed. Preview and confirm before applying.</p>
    </div>

    <app-costing-repair [products]="products"></app-costing-repair>
  `
})
export class CostingRepairPageComponent {
  products: Product[] = [];

  constructor(private readonly productService: ProductService) {
    this.productService.getAll().subscribe({ next: products => this.products = products, error: () => this.products = [] });
  }
}
