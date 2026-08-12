import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { Product, Machine } from '../../models/models';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './dashboard.component.html'
})
export class DashboardComponent implements OnInit {
  products: Product[] = [];
  lowStock: Product[] = [];
  machines: Machine[] = [];

  constructor(
    private productService: ProductService,
    private machineService: MachineService
  ) {}

  ngOnInit(): void {
    this.productService.getAll().subscribe((p) => (this.products = p));
    this.productService.getLowStock().subscribe((p) => (this.lowStock = p));
    this.machineService.getAll().subscribe((m) => {
      console.log('Machines:', m);
      this.machines = m;
    });
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

  // Machine aggregates
  get totalTodayGross(): number {
    return this.machines.reduce((sum, m) => sum + (m.todayGrossRevenue ?? 0), 0);
  }

  get totalTodayNet(): number {
    return this.machines.reduce((sum, m) => sum + (m.todayNetRevenue ?? 0), 0);
  }

  get totalCurrentWeekGross(): number {
    return this.machines.reduce((sum, m) => sum + (m.currentWeekGrossRevenue ?? 0), 0);
  }

  get totalCurrentWeekNet(): number {
    return this.machines.reduce((sum, m) => sum + (m.currentWeekNetRevenue ?? 0), 0);
  }

  get totalLastWeekGross(): number {
    return this.machines.reduce((sum, m) => sum + (m.lastWeekGrossRevenue ?? 0), 0);
  }

  get totalLastWeekNet(): number {
    return this.machines.reduce((sum, m) => sum + (m.lastWeekNetRevenue ?? 0), 0);
  }

  get totalTwoWeeksAgoGross(): number {
    return this.machines.reduce((sum, m) => sum + (m.twoWeeksAgoGrossRevenue ?? 0), 0);
  }

  get totalTwoWeeksAgoNet(): number {
    return this.machines.reduce((sum, m) => sum + (m.twoWeeksAgoNetRevenue ?? 0), 0);
  }
}

