import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { OperatingExpense, OperatingExpenseCategory, OperatingExpensePayload, OperatingExpenseService } from '../../services/operating-expense.service';
import { SupplierService } from '../../services/supplier.service';
import { MachineService } from '../../services/machine.service';
import { Supplier, Machine } from '../../models/models';
import { ObjectUrlCache } from '../shared/object-url-cache';

@Component({
  selector: 'app-operating-expense',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="mb-5 flex items-center justify-between"><div><h1 class="text-2xl font-semibold text-slate-800">Operating Expenses</h1><p class="mt-1 text-sm text-slate-500">Manual expenses only. Nayax processing, commissions, delivery and packaging remain separately sourced.</p></div><button class="rounded-md bg-blue-600 px-4 py-2 text-sm font-semibold text-white" (click)="startCreate()">Add Expense</button></div>
    <section class="mb-5 rounded-xl bg-white p-5 shadow-sm">
      <div class="grid gap-3 md:grid-cols-4"><label class="text-sm">From<input type="date" [(ngModel)]="filters.from" class="mt-1 w-full rounded border p-2"></label><label class="text-sm">To<input type="date" [(ngModel)]="filters.to" class="mt-1 w-full rounded border p-2"></label><label class="text-sm">Category<select [(ngModel)]="filters.category" class="mt-1 w-full rounded border p-2"><option [ngValue]="undefined">All</option>@for (category of categories; track category) {<option [ngValue]="category">{{ categoryLabel(category) }}</option>}</select></label><label class="text-sm">Supplier<select [(ngModel)]="filters.supplierId" class="mt-1 w-full rounded border p-2"><option [ngValue]="undefined">All</option>@for (supplier of suppliers; track supplier.id) {<option [ngValue]="supplier.id">{{ supplier.name }}</option>}</select></label></div><button class="mt-3 rounded border px-3 py-2 text-sm" (click)="load()">Apply filters</button>
    </section>
    @if (editing) { <section class="mb-5 rounded-xl border border-blue-200 bg-blue-50 p-5"><h2 class="mb-4 text-lg font-semibold">{{ editing.id ? 'Edit Expense' : 'Add Expense' }}</h2><div class="grid gap-3 md:grid-cols-3"><label class="text-sm">Date<input type="date" [(ngModel)]="form.expenseDate" class="mt-1 w-full rounded border p-2"></label><label class="text-sm">Category<select [(ngModel)]="form.category" class="mt-1 w-full rounded border p-2">@for (category of categories; track category) {<option [ngValue]="category">{{ categoryLabel(category) }}</option>}</select></label><label class="text-sm">Description<input [(ngModel)]="form.description" class="mt-1 w-full rounded border p-2"></label><label class="text-sm">Amount ex GST<input type="number" step="0.01" [(ngModel)]="form.amountExGst" class="mt-1 w-full rounded border p-2"></label><label class="text-sm">GST<input type="number" step="0.01" [(ngModel)]="form.gstAmount" class="mt-1 w-full rounded border p-2"></label><label class="text-sm">Total<input type="number" step="0.01" [(ngModel)]="form.totalAmount" class="mt-1 w-full rounded border p-2"></label><label class="text-sm">Supplier<select [(ngModel)]="form.supplierId" class="mt-1 w-full rounded border p-2"><option [ngValue]="null">Whole business</option>@for (supplier of suppliers; track supplier.id) {<option [ngValue]="supplier.id">{{ supplier.name }}</option>}</select></label><label class="text-sm">Machine (optional)<select [(ngModel)]="form.machineId" class="mt-1 w-full rounded border p-2"><option [ngValue]="null">None</option>@for (machine of machines; track machine.machineID) {<option [ngValue]="machine.machineID">{{ machine.machineName || machine.machineNumber || machine.machineID }}</option>}</select></label><label class="text-sm">Supporting Document<span class="block text-xs text-slate-500">Receipt, invoice or other supporting document</span><input type="file" accept=".jpg,.jpeg,.png,.pdf,.webp,.heic,image/*,application/pdf" (change)="onAttachmentSelected($event)" class="mt-1 w-full rounded border p-2"></label><label class="text-sm">Notes<input [(ngModel)]="form.notes" class="mt-1 w-full rounded border p-2"></label></div>@if (editing.attachmentFileName) {<button type="button" class="mt-3 inline-block text-sm text-blue-600 underline" (click)="viewAttachment(editing)">View document</button>}<div class="mt-4 flex gap-2"><button class="rounded bg-blue-600 px-4 py-2 text-white" (click)="save()">Save</button><button class="rounded border px-4 py-2" (click)="cancel()">Cancel</button></div></section> }
    <div class="overflow-x-auto rounded-xl bg-white shadow-sm"><table class="min-w-full text-sm"><thead><tr class="bg-slate-50 text-left"><th class="px-4 py-3">Date</th><th class="px-4 py-3">Category</th><th class="px-4 py-3">Description</th><th class="px-4 py-3">Supplier</th><th class="px-4 py-3">Document</th><th class="px-4 py-3">Ex GST</th><th class="px-4 py-3">GST</th><th class="px-4 py-3">Total</th><th class="px-4 py-3"></th></tr></thead><tbody>@for (expense of expenses; track expense.id) {<tr class="border-t"><td class="px-4 py-3">{{ expense.expenseDate | date:'yyyy-MM-dd' }}</td><td class="px-4 py-3">{{ categoryLabel(expense.category) }}</td><td class="px-4 py-3">{{ expense.description }}</td><td class="px-4 py-3">{{ expense.supplierName || '—' }}</td><td class="px-4 py-3">@if (expense.attachmentFileName) {<button type="button" class="text-blue-600 underline" (click)="viewAttachment(expense)">View</button>} @else {—}</td><td class="px-4 py-3">{{ expense.amountExGst | currency }}</td><td class="px-4 py-3">{{ expense.gstAmount | currency }}</td><td class="px-4 py-3">{{ expense.totalAmount | currency }}</td><td class="px-4 py-3"><button class="mr-2 text-blue-600" (click)="startEdit(expense)">Edit</button><button class="text-red-600" (click)="remove(expense)">Delete</button></td></tr>}</tbody><tfoot><tr class="border-t font-semibold"><td colspan="5" class="px-4 py-3">Totals</td><td class="px-4 py-3">{{ total('amountExGst') | currency }}</td><td class="px-4 py-3">{{ total('gstAmount') | currency }}</td><td class="px-4 py-3">{{ total('totalAmount') | currency }}</td><td></td></tr></tfoot></table></div>
  `
})
export class OperatingExpenseComponent implements OnInit, OnDestroy {
  readonly categories = Object.values(OperatingExpenseCategory).filter(x => typeof x === 'number') as OperatingExpenseCategory[];
  readonly categoryNames = ['NayaxMonthlyFee', 'Insurance', 'RepairsAndMaintenance', 'Software', 'Accounting', 'PhoneInternet', 'VehicleTravel', 'BankFees', 'Other'];
  expenses: OperatingExpense[] = []; suppliers: Supplier[] = []; machines: Machine[] = [];
  editing: OperatingExpense | null = null;
  selectedAttachment: File | null = null;
  filters: { from?: string; to?: string; category?: OperatingExpenseCategory; supplierId?: number } = {};
  form: OperatingExpensePayload = this.emptyForm();
  // Supporting documents are protected by the API, so they are fetched through HttpClient
  // (which attaches the bearer token) and opened from a temporary object URL.
  private readonly objectUrls = new ObjectUrlCache();
  constructor(public service: OperatingExpenseService, private supplierService: SupplierService, private machineService: MachineService) {}
  ngOnInit(): void { this.load(); this.supplierService.getAll().subscribe(x => this.suppliers = x); this.machineService.getAll().subscribe(x => this.machines = x); }
  ngOnDestroy(): void { this.objectUrls.releaseAll(); }
  viewAttachment(expense: OperatingExpense): void {
    this.service.getAttachment(expense.id).subscribe({
      next: blob => window.open(this.objectUrls.create(blob), '_blank', 'noopener'),
      error: err => console.error('Failed to open supporting document', err)
    });
  }
  load(): void { this.service.getAll(this.filters).subscribe(x => this.expenses = x); }
  startCreate(): void { this.editing = { id: 0, ...this.emptyForm() }; this.form = this.emptyForm(); this.selectedAttachment = null; }
  startEdit(expense: OperatingExpense): void { this.editing = expense; this.selectedAttachment = null; this.form = { expenseDate: expense.expenseDate.substring(0, 10), category: expense.category, description: expense.description, amountExGst: expense.amountExGst, gstAmount: expense.gstAmount, totalAmount: expense.totalAmount, supplierId: expense.supplierId, siteId: expense.siteId, machineId: expense.machineId, servicePeriodStart: expense.servicePeriodStart, servicePeriodEnd: expense.servicePeriodEnd, notes: expense.notes }; }
  onAttachmentSelected(event: Event): void { this.selectedAttachment = (event.target as HTMLInputElement).files?.[0] ?? null; }
  cancel(): void { this.editing = null; this.selectedAttachment = null; }
  save(): void { if (!this.form.description.trim()) return; const request = this.editing?.id ? this.service.update(this.editing.id, this.form, this.selectedAttachment) : this.service.create(this.form, this.selectedAttachment); request.subscribe(() => { this.editing = null; this.selectedAttachment = null; this.load(); }); }
  remove(expense: OperatingExpense): void { if (confirm(`Delete expense "${expense.description}"?`)) this.service.delete(expense.id).subscribe(() => this.load()); }
  categoryLabel(category: OperatingExpenseCategory): string { return this.categoryNames[Number(category)]?.replace(/([a-z])([A-Z])/g, '$1 $2') ?? 'Other'; }
  total(field: 'amountExGst' | 'gstAmount' | 'totalAmount'): number { return this.expenses.reduce((sum, item) => sum + Number(item[field] || 0), 0); }
  private emptyForm(): OperatingExpensePayload { return { expenseDate: new Date().toISOString().substring(0, 10), category: OperatingExpenseCategory.Other, description: '', amountExGst: 0, gstAmount: 0, totalAmount: 0, supplierId: null, siteId: null, machineId: null, notes: null }; }
}
