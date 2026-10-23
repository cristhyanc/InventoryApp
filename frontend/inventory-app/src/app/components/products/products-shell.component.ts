import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

/**
 * The Products area's shared header: the page title and the view navigation, and nothing else.
 *
 * It holds no write action since issue #825 - "New Product" moved into the All Products
 * search/filter toolbar (`ProductListComponent`), which is the one view that creates a product, so
 * this component no longer reads the support-session state either: there is nothing here to hide.
 */
@Component({
  selector: 'app-products-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './products-shell.component.html'
})
export class ProductsShellComponent {}
