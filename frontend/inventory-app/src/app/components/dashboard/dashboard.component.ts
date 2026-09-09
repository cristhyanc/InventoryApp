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
  isLoadingSites = false;
  isLoadingMachines = false;
  isLoadingLowStock = false;

  constructor(
    private productService: ProductService,
    private machineService: MachineService,
    private siteService: SiteService
  ) {}

  ngOnInit(): void {
    this.refreshProducts();
    this.refreshMachines();
    this.refreshSites();
  }

  refreshSites(): void {
    this.isLoadingSites = true;
    this.siteService.getAll().subscribe({
      next: (sites) => {
        this.sites = sites;
      },
      error: () => {
        this.sites = [];
      },
      complete: () => {
        this.isLoadingSites = false;
      }
    });
  }

  refreshProducts(): void {
    this.isLoadingLowStock = true;
    this.productService.getAll().subscribe((p) => (this.products = p));
    this.productService.getLowStock().subscribe({
      next: (p) => {
        this.lowStock = p.filter((product) => product.isActive !== false);
      },
      error: () => {
        this.lowStock = [];
      },
      complete: () => {
        this.isLoadingLowStock = false;
      }
    });
  }

  refreshMachines(): void {
    this.isLoadingMachines = true;
    this.machineService.getAll().subscribe({
      next: (m) => {
        this.machines = m;
      },
      error: () => {
        this.machines = [];
      },
      complete: () => {
        this.isLoadingMachines = false;
      }
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

  get totalTodaySales(): number {
    return this.machines.reduce((sum, m) => sum + (m.todayGrossRevenue ?? 0), 0);
  }

  get totalTodayDirectProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.todayDirectProfit ?? 0), 0);
  }

  get isTodayProfitComplete(): boolean {
    return this.machines.every(m => m.todayDirectProfit != null);
  }

  get totalCurrentWeekSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.currentWeekGrossRevenue ?? 0), 0);
  }

  get totalCurrentWeekDirectProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.currentWeekDirectProfit ?? 0), 0);
  }

  get isCurrentWeekProfitComplete(): boolean {
    return this.machines.every(m => m.currentWeekDirectProfit != null);
  }

  get totalPreviousComparableWeekSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.previousComparableWeekGrossRevenue ?? m.lastWeekGrossRevenue ?? 0), 0);
  }

  get totalLastWeekSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.lastWeekGrossRevenue ?? 0), 0);
  }

  get totalLastWeekDirectProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.lastWeekDirectProfit ?? 0), 0);
  }

  get isLastWeekProfitComplete(): boolean {
    return this.machines.every(m => m.lastWeekDirectProfit != null);
  }

  get totalMonthToDateSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.monthToDateGrossRevenue ?? 0), 0);
  }

  get totalMonthToDateDirectProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.monthToDateDirectProfit ?? 0), 0);
  }

  get isMonthToDateProfitComplete(): boolean {
    return this.machines.every(m => m.monthToDateDirectProfit != null);
  }

  get totalNetMargin(): number {
    return this.margin(this.totalCurrentWeekDirectProfit, this.totalCurrentWeekSales);
  }

  netMargin(directProfit: number, sales: number): number {
    return this.margin(directProfit, sales);
  }

  trend(current: number, previous: number): number | null {
    return previous === 0 ? null : ((current - previous) / previous) * 100;
  }

  trendLabel(current: number, previous: number): string {
    const value = this.trend(current, previous);
    if (value === null) return 'No prior sales';
    return `${value >= 0 ? '↑' : '↓'} ${Math.abs(value).toFixed(1)}%`;
  }

  trendClass(current: number, previous: number): string {
    const value = this.trend(current, previous);
    if (value === null) return 'text-slate-500';
    return value >= 0 ? 'text-emerald-600' : 'text-rose-600';
  }

  private margin(netProfit: number, sales: number): number {
    return sales === 0 ? 0 : (netProfit / sales) * 100;
  }

  siteStockClass(site: Site): string {
    if (site.totalStockPercentage < 30) return 'bg-red-100 text-red-700';
    if (site.totalStockPercentage < 80) return 'bg-yellow-100 text-yellow-700';
    return 'bg-green-100 text-green-700';
  }
}
