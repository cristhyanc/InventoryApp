import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { SiteService } from '../../services/site.service';
import { NayaxSalesSyncService } from '../../services/nayax-sales-sync.service';
import { InventoryValuationSummary, Product, Machine, Site } from '../../models/models';
import { trendLabel, trendClass } from '../../formatting/revenue-trend';
import { siteStockClass } from '../../formatting/site-stock-status';

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
  isSalesSyncFailed = false;
  inventoryValuation: InventoryValuationSummary | null = null;
  isLoadingInventoryValuation = false;

  constructor(
    private productService: ProductService,
    private machineService: MachineService,
    private siteService: SiteService,
    private nayaxSalesSyncService: NayaxSalesSyncService
  ) {}

  ngOnInit(): void {
    this.refreshProducts();
    this.refreshSalesDashboard();
    this.refreshInventoryValuation();
  }

  /**
   * Coordinates the home dashboard's Sites and Machines figures so both read the same freshness
   * boundary (issue #187): the latest Nayax sales are synchronized once, then Sites and Machines
   * are loaded from the resulting persisted `NayaxSales` data. If synchronization fails, existing
   * persisted data is still loaded rather than fabricated as zero, but `isSalesSyncFailed` flags
   * that the figures may be stale so the UI never presents them as freshly synchronized.
   */
  refreshSalesDashboard(): void {
    this.isSalesSyncFailed = false;
    this.isLoadingSites = true;
    this.isLoadingMachines = true;
    this.nayaxSalesSyncService.syncLatest().subscribe({
      next: () => {
        this.refreshMachines();
        this.refreshSites();
      },
      error: () => {
        this.isSalesSyncFailed = true;
        this.refreshMachines();
        this.refreshSites();
      }
    });
  }

  refreshInventoryValuation(): void {
    this.isLoadingInventoryValuation = true;
    this.productService.getInventoryValuationSummary().subscribe({
      next: (summary) => {
        this.inventoryValuation = summary;
      },
      error: () => {
        this.inventoryValuation = null;
      },
      complete: () => {
        this.isLoadingInventoryValuation = false;
      }
    });
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

  /**
   * The authoritative cost-basis inventory value from `GetInventoryValuationSummary`, never
   * calculated from `Product.unitPrice`/`quantityInStock` on the frontend. `null`/incomplete
   * costing is shown as unavailable, never as a real `$0.00`.
   */
  get inventoryValueDisplay(): string {
    if (!this.inventoryValuation || !this.inventoryValuation.isComplete || this.inventoryValuation.totalInventoryValue == null) {
      return 'Unavailable';
    }
    return `$${this.inventoryValuation.totalInventoryValue.toFixed(2)}`;
  }

  get inventoryValueHelpText(): string {
    if (!this.inventoryValuation) return 'Business-owned inventory at cost';
    if (this.inventoryValuation.isComplete) return 'Business-owned inventory at cost';
    return `${this.inventoryValuation.productsWithUnknownCost} of ${this.inventoryValuation.totalProducts} products missing cost data`;
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

  get totalDirectMargin(): number {
    return this.margin(this.totalCurrentWeekDirectProfit, this.totalCurrentWeekSales);
  }

  directMargin(directProfit: number, sales: number): number {
    return this.margin(directProfit, sales);
  }

  trendLabel(current: number, previous: number): string {
    return trendLabel(current, previous);
  }

  trendClass(current: number, previous: number): string {
    return trendClass(current, previous);
  }

  private margin(profit: number, sales: number): number {
    return sales === 0 ? 0 : (profit / sales) * 100;
  }

  siteStockClass(site: Site): string {
    return siteStockClass(site);
  }

  /**
   * "After I fill the machines to their current required level, how much storage stock will I
   * have left?" (issue #243). A pure display derivation of the already-authoritative
   * `quantityInStock`/`machineReplenishmentNeed` the Reorder Alerts row already carries; it never
   * feeds back into `needToOrder` or any other reorder calculation.
   */
  stockAfterMachineNeed(product: Product): number {
    return product.quantityInStock - product.machineReplenishmentNeed;
  }

  stockAfterMachineNeedClass(product: Product): string {
    return this.stockAfterMachineNeed(product) < 0 ? 'text-red-600 font-semibold' : 'text-slate-600';
  }
}
