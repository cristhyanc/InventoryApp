import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { SiteService } from '../../services/site.service';
import { Product, Machine, Site } from '../../models/models';

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
  sites: Site[] = [];

  constructor(
    private productService: ProductService,
    private machineService: MachineService,
    private siteService: SiteService
  ) {}

  ngOnInit(): void {
    this.productService.getAll().subscribe((p) => (this.products = p));
    this.productService.getLowStock().subscribe((p) => (this.lowStock = p.filter((product) => product.isActive !== false)));
    this.machineService.getAll().subscribe((m) => {
      this.machines = m;
    });
    this.siteService.getAll().subscribe((sites) => {
      this.sites = sites;
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

  siteStockClass(site: Site): string {
    if (site.totalStockPercentage < 30) return 'bg-red-100 text-red-700';
    if (site.totalStockPercentage < 80) return 'bg-yellow-100 text-yellow-700';
    return 'bg-green-100 text-green-700';
  }
}
