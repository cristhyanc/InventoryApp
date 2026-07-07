import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ProductService } from '../../services/product.service';
import { Product } from '../../models/models';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './dashboard.component.html'
})
export class DashboardComponent implements OnInit {
  products: Product[] = [];
  lowStock: Product[] = [];

  constructor(private productService: ProductService) {}

  ngOnInit(): void {
    this.productService.getAll().subscribe((p) => (this.products = p));
    this.productService.getLowStock().subscribe((p) => (this.lowStock = p));
  }

  get totalProducts(): number {
    return this.products.length;
  } 

  get totalUnitsInStock(): number {
    return this.products.reduce((sum, p) => sum + p.quantityInStock, 0);
  }

  get inventoryValue(): number {
    return this.products.reduce((sum, p) => sum + p.quantityInStock * p.unitPrice, 0);
  }
}
