import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { OperatingExpense, OperatingExpenseCategory, OperatingExpensePayload, OperatingExpenseService } from '../../services/operating-expense.service';
import { SupplierService } from '../../services/supplier.service';
import { MachineService } from '../../services/machine.service';
import { Supplier, Machine } from '../../models/models';
import { ObjectUrlCache } from '../shared/object-url-cache';
import { IconComponent } from '../shared/icon.component';

@Component({
  selector: 'app-operating-expense',
  standalone: true,
  imports: [CommonModule, FormsModule, IconComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Operating Expenses</h1>
          <p class="page-subtitle">Manual expenses only. Nayax processing, commissions, delivery and packaging remain separately sourced.</p>
        </div>
        <div class="page-actions">
          <button type="button" class="btn btn-primary" (click)="startCreate()">
            <app-icon name="add" [size]="16" />
            Add Expense
          </button>
        </div>
      </header>

      <section class="card">
        <div class="card-body">
          <div class="grid gap-3 md:grid-cols-4">
            <label class="field">
              <span class="field-label">From</span>
              <input type="date" [(ngModel)]="filters.from">
            </label>
            <label class="field">
              <span class="field-label">To</span>
              <input type="date" [(ngModel)]="filters.to">
            </label>
            <label class="field">
              <span class="field-label">Category</span>
              <select [(ngModel)]="filters.category">
                <option [ngValue]="undefined">All</option>
                @for (category of categories; track category) {
                  <option [ngValue]="category">{{ categoryLabel(category) }}</option>
                }
              </select>
            </label>
            <label class="field">
              <span class="field-label">Supplier</span>
              <select [(ngModel)]="filters.supplierId">
                <option [ngValue]="undefined">All</option>
                @for (supplier of suppliers; track supplier.id) {
                  <option [ngValue]="supplier.id">{{ supplier.name }}</option>
                }
              </select>
            </label>
          </div>
          <button type="button" class="btn btn-secondary btn-sm mt-3" (click)="load()">Apply filters</button>
        </div>
      </section>

      @if (editing) {
        <section class="card">
          <div class="card-header">
            <h2 class="card-title">{{ editing.id ? 'Edit Expense' : 'Add Expense' }}</h2>
          </div>
          <div class="card-body">
            <div class="grid gap-3 md:grid-cols-3">
              <label class="field">
                <span class="field-label">Date</span>
                <input type="date" [(ngModel)]="form.expenseDate">
              </label>
              <label class="field">
                <span class="field-label">Category</span>
                <select [(ngModel)]="form.category">
                  @for (category of categories; track category) {
                    <option [ngValue]="category">{{ categoryLabel(category) }}</option>
                  }
                </select>
              </label>
              <label class="field">
                <span class="field-label">Description</span>
                <input [(ngModel)]="form.description">
              </label>
              <label class="field">
                <span class="field-label">Amount ex GST</span>
                <input type="number" step="0.01" [(ngModel)]="form.amountExGst">
              </label>
              <label class="field">
                <span class="field-label">GST</span>
                <input type="number" step="0.01" [(ngModel)]="form.gstAmount">
              </label>
              <label class="field">
                <span class="field-label">Total</span>
                <input type="number" step="0.01" [(ngModel)]="form.totalAmount">
              </label>
              <label class="field">
                <span class="field-label">Supplier</span>
                <select [(ngModel)]="form.supplierId">
                  <option [ngValue]="null">Whole business</option>
                  @for (supplier of suppliers; track supplier.id) {
                    <option [ngValue]="supplier.id">{{ supplier.name }}</option>
                  }
                </select>
              </label>
              <label class="field">
                <span class="field-label">Machine (optional)</span>
                <select [(ngModel)]="form.machineId">
                  <option [ngValue]="null">None</option>
                  @for (machine of machines; track machine.machineID) {
                    <option [ngValue]="machine.machineID">{{ machine.machineName || machine.machineNumber || machine.machineID }}</option>
                  }
                </select>
              </label>
              <label class="field">
                <span class="field-label">Supporting Document</span>
                <span class="field-hint">Receipt, invoice or other supporting document</span>
                <input type="file" accept=".jpg,.jpeg,.png,.pdf,.webp,.heic,image/*,application/pdf" (change)="onAttachmentSelected($event)">
              </label>
              <label class="field">
                <span class="field-label">Notes</span>
                <input [(ngModel)]="form.notes">
              </label>
            </div>
            @if (editing.attachmentFileName) {
              <button type="button" class="btn btn-link btn-sm mt-3" (click)="viewAttachment(editing)">View document</button>
            }
            <div class="page-actions mt-4">
              <button type="button" class="btn btn-primary" (click)="save()">Save</button>
              <button type="button" class="btn btn-secondary" (click)="cancel()">Cancel</button>
            </div>
          </div>
        </section>
      }

      <section class="card">
        <div class="card-body p-0">
          <div class="overflow-x-auto">
            <table class="table">
              <thead class="table-head">
                <tr>
                  <th scope="col" class="table-cell">Date</th>
                  <th scope="col" class="table-cell">Category</th>
                  <th scope="col" class="table-cell">Description</th>
                  <th scope="col" class="table-cell">Supplier</th>
                  <th scope="col" class="table-cell">Document</th>
                  <th scope="col" class="table-cell table-num">Ex GST</th>
                  <th scope="col" class="table-cell table-num">GST</th>
                  <th scope="col" class="table-cell table-num">Total</th>
                  <th scope="col" class="table-cell"></th>
                </tr>
              </thead>
              <tbody>
                @for (expense of expenses; track expense.id) {
                  <tr class="table-row">
                    <td class="table-cell">{{ expense.expenseDate | date:'yyyy-MM-dd' }}</td>
                    <td class="table-cell">{{ categoryLabel(expense.category) }}</td>
                    <td class="table-cell">{{ expense.description }}</td>
                    <td class="table-cell">{{ expense.supplierName || '—' }}</td>
                    <td class="table-cell">
                      @if (expense.attachmentFileName) {
                        <button type="button" class="btn btn-link btn-sm" (click)="viewAttachment(expense)">View</button>
                      } @else {
                        —
                      }
                    </td>
                    <td class="table-cell table-num">{{ expense.amountExGst | currency }}</td>
                    <td class="table-cell table-num">{{ expense.gstAmount | currency }}</td>
                    <td class="table-cell table-num">{{ expense.totalAmount | currency }}</td>
                    <td class="table-cell">
                      <div class="flex gap-2">
                        <button type="button" class="btn btn-sm btn-secondary" (click)="startEdit(expense)">
                          <app-icon name="edit" [size]="16" />
                          Edit
                        </button>
                        <button type="button" class="btn btn-sm btn-danger" (click)="remove(expense)">
                          <app-icon name="delete" [size]="16" />
                          Delete
                        </button>
                      </div>
                    </td>
                  </tr>
                }
              </tbody>
              <tfoot class="bg-md-gray-100 font-semibold">
                <tr>
                  <td colspan="5" class="table-cell">Totals</td>
                  <td class="table-cell table-num">{{ total('amountExGst') | currency }}</td>
                  <td class="table-cell table-num">{{ total('gstAmount') | currency }}</td>
                  <td class="table-cell table-num">{{ total('totalAmount') | currency }}</td>
                  <td class="table-cell"></td>
                </tr>
              </tfoot>
            </table>
          </div>
        </div>
      </section>
    </div>
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
