import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ProductReport, ProductRow, ReportingFilter, ReportingService } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';
import { IconComponent } from '../shared/icon.component';

@Component({ selector: 'app-product-report', standalone: true, imports: [CommonModule, ReportFiltersComponent, IconComponent], template: `
<div class="page">
  <header class="page-header">
    <h1 class="page-title">Product Profitability</h1>
    <div class="page-actions">
      <button type="button" class="btn btn-sm btn-secondary" (click)="export('csv','product-profitability')"><app-icon name="download" [size]="18" />Export CSV</button>
      <button type="button" class="btn btn-sm btn-secondary" (click)="export('xlsx','product-profitability')"><app-icon name="download" [size]="18" />Export XLSX</button>
    </div>
  </header>
  <app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
  @if (loading) { <div class="card"><div class="card-body text-center"><span class="value-muted">Loading report...</span></div></div> }
  @else if (error) { <div class="alert alert-danger">{{ error }}</div> }
  @else if (report) {
    <div class="card overflow-x-auto"><table class="table"><thead class="table-head"><tr><th scope="col" class="table-cell" [attr.aria-sort]="ariaSort('productName')"><button type="button" class="font-bold uppercase hover:underline" (click)="sortBy('productName')">{{ sortLabel('Product', 'productName') }}</button></th><th scope="col" class="table-cell" [attr.aria-sort]="ariaSort('categoryName')"><button type="button" class="font-bold uppercase hover:underline" (click)="sortBy('categoryName')">{{ sortLabel('Category', 'categoryName') }}</button></th><th scope="col" class="table-cell" [attr.aria-sort]="ariaSort('quantity')"><button type="button" class="font-bold uppercase hover:underline" (click)="sortBy('quantity')">{{ sortLabel('Units', 'quantity') }}</button></th><th scope="col" class="table-cell" [attr.aria-sort]="ariaSort('sales')"><button type="button" class="font-bold uppercase hover:underline" (click)="sortBy('sales')">{{ sortLabel('Revenue', 'sales') }}</button></th><th scope="col" class="table-cell" [attr.aria-sort]="ariaSort('costOfGoods')"><button type="button" class="font-bold uppercase hover:underline" (click)="sortBy('costOfGoods')">{{ sortLabel('COGS', 'costOfGoods') }}</button></th><th scope="col" class="table-cell" [attr.aria-sort]="ariaSort('grossProfit')"><button type="button" class="font-bold uppercase hover:underline" (click)="sortBy('grossProfit')">{{ sortLabel('Gross Profit', 'grossProfit') }}</button></th><th scope="col" class="table-cell" [attr.aria-sort]="ariaSort('lastCost')"><button type="button" class="font-bold uppercase hover:underline" (click)="sortBy('lastCost')">{{ sortLabel('Last Cost', 'lastCost') }}</button></th><th scope="col" class="table-cell" [attr.aria-sort]="ariaSort('lowestCost')"><button type="button" class="font-bold uppercase hover:underline" (click)="sortBy('lowestCost')">{{ sortLabel('Lowest Cost', 'lowestCost') }}</button></th><th scope="col" class="table-cell" [attr.aria-sort]="ariaSort('savingPerUnit')"><button type="button" class="font-bold uppercase hover:underline" (click)="sortBy('savingPerUnit')">{{ sortLabel('Saving / unit', 'savingPerUnit') }}</button></th></tr></thead><tbody>@for (row of sortedRows; track row.productId ?? row.productName) {<tr class="table-row" [ngClass]="{ 'bg-md-warning/10': row.isUnmapped }"><td class="table-cell">{{ row.productName }} @if (row.isUnmapped) {<span class="text-md-warning-text">(Unmapped Nayax product)</span>}</td><td class="table-cell">{{ row.categoryName || '—' }}</td><td class="table-cell">{{ row.quantity }}</td><td class="table-cell"><div>{{ money(row.sales) }}</div><div class="value-muted">Card {{ money(row.cardRevenue) }} · Cash {{ money(row.cashRevenue) }}</div></td><td class="table-cell"><div [class.text-md-warning-text]="!row.isCogsComplete">{{ cogsDisplay(row) }}</div>@if (!row.isCogsComplete) {<div class="mt-1 text-md-warning-text" [title]="cogsHelpText()">{{ cogsHelpText() }}</div>}</td><td class="table-cell"><div [class.text-md-warning-text]="!row.isCogsComplete">{{ profitDisplay(row) }}</div>@if (row.isCogsComplete && row.marginPercent != null) {<div class="mt-1 value-muted">{{ row.marginPercent | number:'1.1-1' }}% margin</div>} @else if (!row.isCogsComplete) {<div class="mt-1 text-md-warning-text">{{ cogsHelpText() }}</div>}</td><td class="table-cell"><div>{{ lastCostDisplay(row) }}</div>@if (row.lastCost != null) {<div class="mt-1 value-muted">{{ supplierDisplay(row.lastCostSupplierName) }}</div>}</td><td class="table-cell"><div>{{ lowestCostDisplay(row) }}</div>@if (row.lowestCost != null) {<div class="mt-1 value-muted">{{ supplierDisplay(row.lowestCostSupplierName) }}</div>}</td><td class="table-cell">{{ savingDisplay(row) }}</td></tr>}</tbody></table></div>
  }
</div>
` })
export class ProductReportComponent extends ReportPageBase<ProductReport> {
  sortColumn: ProductSortColumn = 'sales';
  sortDirection: SortDirection = 'desc';

  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<ProductReport> { return this.reports.products(filter); }

  cogsDisplay(row: ProductRow): string {
    if (row.isCogsComplete) {
      return this.money(row.costOfGoods);
    }

    const costedTransactions = (row.transactionCount ?? 0) - (row.uncostedTransactionCount ?? 0);
    if (costedTransactions > 0) {
      return `Partial ${this.money(row.partialCostOfGoods)}`;
    }

    return 'Unavailable';
  }

  profitDisplay(row: ProductRow): string {
    if (row.isCogsComplete) {
      return this.money(row.grossProfit);
    }

    return 'Unavailable';
  }

  cogsHelpText(): string {
    return 'Some sales do not have historical cost data, so this value is incomplete.';
  }

  lastCostDisplay(row: ProductRow): string {
    return row.lastCost == null ? '—' : this.money(row.lastCost);
  }

  lowestCostDisplay(row: ProductRow): string {
    return row.lowestCost == null ? '—' : this.money(row.lowestCost);
  }

  savingDisplay(row: ProductRow): string {
    return row.savingPerUnit == null ? '—' : this.money(row.savingPerUnit);
  }

  supplierDisplay(supplierName: string | null | undefined): string {
    return supplierName ?? 'None';
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

type ProductSortColumn = 'productName' | 'categoryName' | 'quantity' | 'sales' | 'costOfGoods' | 'grossProfit'
  | 'lastCost' | 'lowestCost' | 'savingPerUnit';
type SortDirection = 'asc' | 'desc';
