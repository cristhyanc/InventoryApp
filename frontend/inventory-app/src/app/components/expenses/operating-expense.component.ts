import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { OperatingExpense, OperatingExpenseCategory, OperatingExpensePayload, OperatingExpenseService } from '../../services/operating-expense.service';
import { SupplierService } from '../../services/supplier.service';
import { LocalMachineService } from '../../services/local-machine.service';
import { Supplier, LocalMachine } from '../../models/models';
import { ObjectUrlCache } from '../shared/object-url-cache';
import { IconComponent } from '../shared/icon.component';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { SupportSessionService } from '../../support-session/support-session.service';

/**
 * Operating Expenses: the manual expenses a business records itself.
 *
 * **Nothing on this page needs Nayax to work, and without Nayax it stops mentioning it** (issue
 * #728, decision P9 of issue #329).
 *
 * - The **machine** field attributes an expense to one of the business's **own** machine records,
 *   read from `/api/local-machines` (issue #746, G2b of #329). It needs no provider: the id it
 *   submits is `LocalMachine.id`, which is exactly what `OperatingExpense.MachineId` holds (issue
 *   #780). A **new** expense is offered only the **active** machines; an expense being edited keeps
 *   its own machine on the list even once it has been deactivated, because the cost was incurred
 *   while that machine was in service and retiring it must not silently detach the expense - see
 *   `machineOptions`. A read that **failed** reports itself where the field was and one still
 *   outstanding says it is loading; neither is ever presented as "no machines".
 * - The **"Nayax monthly fee"** category and the Nayax wording in the page subtitle need only Nayax
 *   to be part of this business. Without it the category is not offered for a **new** expense and
 *   the subtitle drops the Nayax sentence - but an expense already recorded under it keeps it, in
 *   the table, in the filter and on the edit form, because a recorded expense is a fact about the
 *   past and this page must never quietly reclassify one.
 */
@Component({
  selector: 'app-operating-expense',
  standalone: true,
  imports: [CommonModule, FormsModule, IconComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Operating Expenses</h1>
          <p class="page-subtitle" data-testid="expenses-subtitle">{{ subtitle }}</p>
        </div>
        <div class="page-actions">
          @if (!sessionActive) {
            <button type="button" class="btn btn-primary" (click)="startCreate()">
              <app-icon name="add" [size]="16" />
              Add Expense
            </button>
          }
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
                  @for (category of formCategories; track category) {
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
              @if (machinesError) {
                <div class="field">
                  <span class="field-label">Machine (optional)</span>
                  <p data-testid="machine-field-error" class="text-md-body text-md-danger-text">{{ machinesError }}</p>
                </div>
              } @else if (machinesLoading) {
                <div class="field">
                  <span class="field-label">Machine (optional)</span>
                  <output data-testid="machine-field-loading" class="field-hint">Loading machines…</output>
                </div>
              } @else if (machineFieldShown) {
                <label class="field">
                  <span class="field-label">Machine (optional)</span>
                  <select id="expense-machine" [(ngModel)]="form.machineId">
                    <option [ngValue]="null">None</option>
                    @for (machine of machineOptions; track machine.id) {
                      <option [ngValue]="machine.id">{{ machine.name }}</option>
                    }
                  </select>
                </label>
              }
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
              <button type="button" class="btn btn-primary" [disabled]="sessionActive" (click)="save()">Save</button>
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
                      @if (!sessionActive) {
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
                      }
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
  expenses: OperatingExpense[] = []; suppliers: Supplier[] = [];
  /** Every machine this business has recorded, active and inactive alike (issue #746). */
  machines: LocalMachine[] = [];
  editing: OperatingExpense | null = null;
  selectedAttachment: File | null = null;
  filters: { from?: string; to?: string; category?: OperatingExpenseCategory; supplierId?: number } = {};
  form: OperatingExpensePayload = this.emptyForm();
  /**
   * Whether a support session is active (issue #659, extending #554): Add/Edit/Delete hide and
   * Save disables itself while it is, because a session grants `ExpensesManage` - the capability
   * this page would otherwise gate those writes on - and the backend refuses the write itself
   * only by its read-only method rule, never by withholding the capability. See
   * `SupportSessionService.active$`.
   */
  sessionActive = false;
  /** Set when the machine list could not be read at all; never shown as "no machines". */
  machinesError = '';
  /** Whether that read is still outstanding, which is not the same as there being no machines. */
  machinesLoading = false;
  /** Whether Nayax is part of this business at all; the Nayax fee category and subtitle need it. */
  providerRecordsAvailable = true;
  // Supporting documents are protected by the API, so they are fetched through HttpClient
  // (which attaches the bearer token) and opened from a temporary object URL.
  private readonly objectUrls = new ObjectUrlCache();
  private readonly sessionActiveSubscription: Subscription;
  constructor(
    public service: OperatingExpenseService,
    private supplierService: SupplierService,
    private localMachineService: LocalMachineService,
    private provider: ProviderAvailabilityService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }
  ngOnInit(): void {
    this.load();
    this.supplierService.getAll().subscribe(x => this.suppliers = x);
    this.loadMachines();
    this.provider.featureOffered('providerRecords').subscribe(available => (this.providerRecordsAvailable = available));
  }

  private loadMachines(): void {
    this.machinesLoading = true;
    this.machinesError = '';
    this.localMachineService.getAll().subscribe({
      next: machines => {
        this.machines = machines ?? [];
        this.machinesLoading = false;
      },
      error: () => {
        this.machines = [];
        this.machinesError = 'The machine list could not be loaded, so an expense cannot be attributed to a machine.';
        this.machinesLoading = false;
      }
    });
  }

  /** The page's own wording: without Nayax it describes only what this business does record. */
  get subtitle(): string {
    return this.providerRecordsAvailable
      ? 'Manual expenses only. Nayax processing, commissions, delivery and packaging remain separately sourced.'
      : 'Manual expenses only. Commissions, delivery and packaging remain separately sourced.';
  }

  /**
   * The categories the form offers. Without a provider the Nayax monthly fee is not one of them -
   * there is no Nayax account to be billed by - unless the expense being edited already carries it,
   * because dropping the category an existing expense is recorded under would silently reclassify
   * it the next time somebody saved the form.
   */
  get formCategories(): OperatingExpenseCategory[] {
    if (this.providerRecordsAvailable || this.form.category === OperatingExpenseCategory.NayaxMonthlyFee) {
      return this.categories;
    }
    return this.categories.filter(category => category !== OperatingExpenseCategory.NayaxMonthlyFee);
  }

  /**
   * The machines an expense may be attributed to (issue #746): the business's **active** machines,
   * plus the machine the expense **being edited** already names, even once that machine has been
   * deactivated. Keeping it is what stops a retired machine's historical expense from losing its
   * attribution the next time somebody saves the form; the backend accepts a deactivated machine
   * for exactly that reason (issue #780). It is taken from the stored expense rather than from the
   * form, so choosing "None" and changing one's mind still offers it.
   */
  get machineOptions(): LocalMachine[] {
    const recorded = this.editing?.machineId ?? null;
    return this.machines.filter(machine => machine.isActive || machine.id === recorded);
  }

  /**
   * Whether an expense may be attributed to a machine at all: only when there is a machine to
   * attribute it to. A failed or outstanding read is reported in the field's place instead.
   */
  get machineFieldShown(): boolean {
    return this.machinesError === '' && !this.machinesLoading && this.machineOptions.length > 0;
  }
  ngOnDestroy(): void { this.objectUrls.releaseAll(); this.sessionActiveSubscription.unsubscribe(); }
  viewAttachment(expense: OperatingExpense): void {
    this.service.getAttachment(expense.id).subscribe({
      next: blob => window.open(this.objectUrls.create(blob), '_blank', 'noopener'),
      error: err => console.error('Failed to open supporting document', err)
    });
  }
  load(): void { this.service.getAll(this.filters).subscribe(x => this.expenses = x); }
  // Checked here as well as hidden/disabled in the template, so the write is actually unavailable
  // rather than merely invisible or inert - the endpoint refuses it either way.
  startCreate(): void { if (this.sessionActive) { return; } this.editing = { id: 0, ...this.emptyForm() }; this.form = this.emptyForm(); this.selectedAttachment = null; }
  startEdit(expense: OperatingExpense): void { if (this.sessionActive) { return; } this.editing = expense; this.selectedAttachment = null; this.form = { expenseDate: expense.expenseDate.substring(0, 10), category: expense.category, description: expense.description, amountExGst: expense.amountExGst, gstAmount: expense.gstAmount, totalAmount: expense.totalAmount, supplierId: expense.supplierId, siteId: expense.siteId, machineId: expense.machineId, servicePeriodStart: expense.servicePeriodStart, servicePeriodEnd: expense.servicePeriodEnd, notes: expense.notes }; }
  onAttachmentSelected(event: Event): void { this.selectedAttachment = (event.target as HTMLInputElement).files?.[0] ?? null; }
  cancel(): void { this.editing = null; this.selectedAttachment = null; }
  save(): void { if (this.sessionActive || !this.form.description.trim()) { return; } const request = this.editing?.id ? this.service.update(this.editing.id, this.form, this.selectedAttachment) : this.service.create(this.form, this.selectedAttachment); request.subscribe(() => { this.editing = null; this.selectedAttachment = null; this.load(); }); }
  remove(expense: OperatingExpense): void { if (!this.sessionActive && confirm(`Delete expense "${expense.description}"?`)) this.service.delete(expense.id).subscribe(() => this.load()); }
  categoryLabel(category: OperatingExpenseCategory): string { return this.categoryNames[Number(category)]?.replace(/([a-z])([A-Z])/g, '$1 $2') ?? 'Other'; }
  total(field: 'amountExGst' | 'gstAmount' | 'totalAmount'): number { return this.expenses.reduce((sum, item) => sum + Number(item[field] || 0), 0); }
  private emptyForm(): OperatingExpensePayload { return { expenseDate: new Date().toISOString().substring(0, 10), category: OperatingExpenseCategory.Other, description: '', amountExGst: 0, gstAmount: 0, totalAmount: 0, supplierId: null, siteId: null, machineId: null, notes: null }; }
}
