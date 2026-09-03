import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { ReportingFilter, ReportingService, ProductReport } from '../../services/reporting.service';
import { ReportPageBase } from './report-page.base';
import { ReportFiltersComponent } from './report-filters.component';

@Component({ selector: 'app-product-report', standalone: true, imports: [CommonModule, ReportFiltersComponent], template: `
<div class="mb-5 flex items-center justify-between"><h1 class="text-2xl font-semibold text-slate-800">Product Profitability</h1><div class="flex gap-2"><button class="rounded-md border px-3 py-2 text-sm" (click)="export('csv','product-profitability')">Export CSV</button><button class="rounded-md border px-3 py-2 text-sm" (click)="export('xlsx','product-profitability')">Export XLSX</button></div></div>
<app-report-filters [from]="from" [to]="to" [machineId]="machineId" [machines]="machines" [period]="period" (fromChange)="from=$event" (toChange)="to=$event" (machineChange)="machineId=$event" (periodChange)="selectPeriod($event)" (apply)="load()" />
@if (loading) { <div class="rounded-xl bg-white p-8 text-center text-slate-500">Loading report...</div> } @else if (error) { <div class="rounded-xl bg-red-50 p-6 text-red-700">{{ error }}</div> } @else if (report) { <div class="overflow-x-auto rounded-xl bg-white shadow-sm"><table class="min-w-full text-sm"><thead><tr class="bg-slate-50 text-left"><th class="px-4 py-3">Product</th><th class="px-4 py-3">Category</th><th class="px-4 py-3">Units</th><th class="px-4 py-3">Revenue</th><th class="px-4 py-3">COGS</th><th class="px-4 py-3">Gross Profit</th></tr></thead><tbody>@for (row of report.rows; track row.productId ?? row.productName) {<tr class="border-t" [class.bg-amber-50]="row.isUnmapped"><td class="px-4 py-3">{{ row.productName }} @if (row.isUnmapped) {<span class="text-xs text-amber-700">(Unmapped Nayax product)</span>}</td><td class="px-4 py-3">{{ row.categoryName || '—' }}</td><td class="px-4 py-3">{{ row.quantity }}</td><td class="px-4 py-3"><div>{{ money(row.sales) }}</div><div class="text-xs text-slate-500">Card {{ money(row.cardRevenue) }} · Cash {{ money(row.cashRevenue) }}</div></td><td class="px-4 py-3">{{ money(row.costOfGoods) }}</td><td class="px-4 py-3">{{ money(row.grossProfit) }}</td></tr>}</tbody></table></div> }
` })
export class ProductReportComponent extends ReportPageBase<ProductReport> {
  constructor(route: ActivatedRoute, reports: ReportingService, machines: MachineService) { super(route, reports, machines); }
  request(filter: ReportingFilter): Observable<ProductReport> { return this.reports.products(filter); }
}
