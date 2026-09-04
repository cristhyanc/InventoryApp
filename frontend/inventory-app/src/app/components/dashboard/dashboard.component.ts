import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ProductService } from '../../services/product.service';
import { MachineService } from '../../services/machine.service';
import { SiteService } from '../../services/site.service';
import { ToastService } from '../../services/toast.service';
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
  importingNayaxSales = false;
  isLoadingSites = false;
  isLoadingMachines = false;
  isLoadingLowStock = false;

  constructor(
    private productService: ProductService,
    private machineService: MachineService,
    private siteService: SiteService,
    private toastService: ToastService
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

  onNayaxSalesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    if (!/\.(xlsx|xls|csv)$/i.test(file.name)) {
      this.toastService.error('Only .xlsx, .xls, or .csv files are supported.');
      input.value = '';
      return;
    }

    this.importNayaxSales(file);
    input.value = '';
  }

  downloadNayaxSalesTemplate(): void {
    const headers = [
      'TransactionID',
      'MachineID',
      'NayaxProductId',
      'MachineName',
      'SettlementValue',
      'PaymentMethod',
      'ProductName',
      'Quantity',
      'MachineAuthorizationTime'
    ];

    const sampleRow = [
      '1001',
      '42',
      '987654',
      'Machine A',
      '12.50',
      'Card',
      'Coke Zero',
      '1',
      '2026-09-02 14:30:00'
    ];

    const csv = [headers.join(','), sampleRow.join(',')].join('\n');
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = 'nayax-sales-import-template.csv';
    document.body.appendChild(anchor);
    anchor.click();
    document.body.removeChild(anchor);
    URL.revokeObjectURL(url);

    this.toastService.info('Template downloaded. Fill in the rows and import it back.');
  }

  importNayaxSales(file: File): void {
    this.importingNayaxSales = true;
    this.machineService.importNayaxSales(file).subscribe({
      next: (result) => {
        this.importingNayaxSales = false;
        this.toastService.success(`Imported ${result.imported} sales, updated ${result.updated}, skipped ${result.skipped}.`);
        this.refreshMachines();
      },
      error: (err) => {
        this.importingNayaxSales = false;
        this.toastService.error(err?.error?.message ?? err?.error ?? 'Failed to import Nayax sales.');
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

  get totalTodayNetProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.todayNetRevenue ?? 0), 0);
  }

  get totalCurrentWeekSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.currentWeekGrossRevenue ?? 0), 0);
  }

  get totalCurrentWeekNetProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.currentWeekNetRevenue ?? 0), 0);
  }

  get totalPreviousComparableWeekSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.previousComparableWeekGrossRevenue ?? m.lastWeekGrossRevenue ?? 0), 0);
  }

  get totalLastWeekSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.lastWeekGrossRevenue ?? 0), 0);
  }

  get totalLastWeekNetProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.lastWeekNetRevenue ?? 0), 0);
  }

  get totalMonthToDateSales(): number {
    return this.machines.reduce((sum, m) => sum + (m.monthToDateGrossRevenue ?? 0), 0);
  }

  get totalMonthToDateNetProfit(): number {
    return this.machines.reduce((sum, m) => sum + (m.monthToDateNetRevenue ?? 0), 0);
  }

  get totalNetMargin(): number {
    return this.margin(this.totalCurrentWeekNetProfit, this.totalCurrentWeekSales);
  }

  netMargin(netProfit: number, sales: number): number {
    return this.margin(netProfit, sales);
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
