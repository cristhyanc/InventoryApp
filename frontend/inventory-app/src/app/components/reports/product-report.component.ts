import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ProductReport, ProductRow, ReportingFilter, ReportingService } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';

@Component({ selector: 'app-product-report', standalone: true, imports: [CommonModule, ReportFiltersComponent], template: `
<div class="mb-5 flex items-center justify-between"><h1 class="text-2xl font-semibold text-slate-800">Product Profitability</h1><div class="flex gap-2"><button class="rounded-md border px-3 py-2 text-sm" (click)="export('csv','product-profitability')">Export CSV</button><button class="rounded-md border px-3 py-2 text-sm" (click)="export('xlsx','product-profitability')">Export XLSX</button></div></div>
<app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
@if (loading) { <div class="rounded-xl bg-white p-8 text-center text-slate-500">Loading report...</div> } @else if (error) { <div class="rounded-xl bg-red-50 p-6 text-red-700">{{ error }}</div> } @else if (report) { <div class="overflow-x-auto rounded-xl bg-white shadow-sm"><table class="min-w-full text-sm"><thead><tr class="bg-slate-50 text-left"><th class="px-4 py-3" [attr.aria-sort]="ariaSort('productName')"><button type="button" class="font-semibold hover:text-sky-700" (click)="sortBy('productName')">{{ sortLabel('Product', 'productName') }}</button></th><th class="px-4 py-3" [attr.aria-sort]="ariaSort('categoryName')"><button type="button" class="font-semibold hover:text-sky-700" (click)="sortBy('categoryName')">{{ sortLabel('Category', 'categoryName') }}</button></th><th class="px-4 py-3" [attr.aria-sort]="ariaSort('quantity')"><button type="button" class="font-semibold hover:text-sky-700" (click)="sortBy('quantity')">{{ sortLabel('Units', 'quantity') }}</button></th><th class="px-4 py-3" [attr.aria-sort]="ariaSort('sales')"><button type="button" class="font-semibold hover:text-sky-700" (click)="sortBy('sales')">{{ sortLabel('Revenue', 'sales') }}</button></th><th class="px-4 py-3" [attr.aria-sort]="ariaSort('costOfGoods')"><button type="button" class="font-semibold hover:text-sky-700" (click)="sortBy('costOfGoods')">{{ sortLabel('COGS', 'costOfGoods') }}</button></th><th class="px-4 py-3" [attr.aria-sort]="ariaSort('grossProfit')"><button type="button" class="font-semibold hover:text-sky-700" (click)="sortBy('grossProfit')">{{ sortLabel('Gross Profit', 'grossProfit') }}</button></th></tr></thead><tbody>@for (row of sortedRows; track row.productId ?? row.productName) {<tr class="border-t" [class.bg-amber-50]="row.isUnmapped"><td class="px-4 py-3">{{ row.productName }} @if (row.isUnmapped) {<span class="text-xs text-amber-700">(Unmapped Nayax product)</span>}</td><td class="px-4 py-3">{{ row.categoryName || '—' }}</td><td class="px-4 py-3">{{ row.quantity }}</td><td class="px-4 py-3"><div>{{ money(row.sales) }}</div><div class="text-xs text-slate-500">Card {{ money(row.cardRevenue) }} · Cash {{ money(row.cashRevenue) }}</div></td><td class="px-4 py-3">{{ money(row.costOfGoods) }}</td><td class="px-4 py-3">{{ money(row.grossProfit) }}</td></tr>}</tbody></table></div> }
` })
export class ProductReportComponent extends ReportPageBase<ProductReport> {
  sortColumn: ProductSortColumn = 'sales';
  sortDirection: SortDirection = 'desc';

  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<ProductReport> { return this.reports.products(filter); }

  override money(value: number | null | undefined): string {
    return value == null ? 'COGS incomplete' : super.money(value);
  }

  get sortedRows(): ProductRow[] {
    return [...(this.report?.rows ?? [])].sort((left, right) => {
      const leftValue = left[this.sortColumn];
      const rightValue = right[this.sortColumn];
      const comparison = typeof leftValue === 'number' && typeof rightValue === 'number'
        ? leftValue - rightValue
        : String(leftValue ?? '').localeCompare(String(rightValue ?? ''));
      return this.sortDirection === 'asc' ? comparison : -comparison;
    });
  }

  sortBy(column: ProductSortColumn): void {
    if (this.sortColumn === column) {
      this.sortDirection = this.sortDirection === 'asc' ? 'desc' : 'asc';
      return;
    }

    this.sortColumn = column;
    this.sortDirection = 'asc';
  }

  ariaSort(column: ProductSortColumn): 'ascending' | 'descending' | 'none' {
    if (this.sortColumn !== column) return 'none';
    return this.sortDirection === 'asc' ? 'ascending' : 'descending';
  }

  sortLabel(label: string, column: ProductSortColumn): string {
    if (this.sortColumn !== column) return label;
    return `${label} (${this.sortDirection === 'asc' ? 'ascending' : 'descending'})`;
  }
}

type ProductSortColumn = 'productName' | 'categoryName' | 'quantity' | 'sales' | 'costOfGoods' | 'grossProfit';
type SortDirection = 'asc' | 'desc';
