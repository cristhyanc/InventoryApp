import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Product } from '../../../models/models';
import { ProductService } from '../../../services/product.service';
import { AvcoTransitionWorkflowComponent } from './avco-transition-workflow.component';

/**
 * Dedicated page for the Inventory AVCO Transition Baseline workflow, split out of
 * `AdminComponent` (issue #390, Admin split 3/3). Per docs/architecture.md § Page composition
 * boundary (issue #191) the page stays a composition boundary: it loads the product list its
 * selector needs and composes `AvcoTransitionWorkflowComponent` through `[products]`, reloading
 * the products when that workflow reports a saved baseline. The preview/apply form, its state,
 * the `InventoryCostTransitionService` calls, the confirmations and the notifications all belong
 * to the workflow component, so no costing calculation or persistence rule exists on this page.
 */
@Component({
  selector: 'app-avco-transition',
  standalone: true,
  imports: [CommonModule, RouterLink, AvcoTransitionWorkflowComponent],
  template: `
    <div class="mb-6">
      <a routerLink="/admin" class="text-sm text-blue-600 hover:underline">&larr; Back to Admin</a>
      <h1 class="mt-2 text-2xl font-semibold text-slate-800">Inventory AVCO Transition Baseline</h1>
      <p class="mt-1 text-sm text-slate-500">A saved baseline is permanent and changes how later sales are costed. Preview and confirm before saving.</p>
    </div>

    <app-avco-transition-workflow
      [products]="products"
      (baselinesSaved)="reloadProducts()">
    </app-avco-transition-workflow>
  `
})
export class AvcoTransitionComponent {
  products: Product[] = [];

  constructor(private readonly productService: ProductService) {
    this.productService.getAll().subscribe({ next: products => this.products = products, error: () => this.products = [] });
  }

  reloadProducts(): void {
    this.productService.getAll().subscribe(products => this.products = products);
  }
}
