import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Params, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { OperatingExpense, OperatingExpenseCategory, OperatingExpenseService } from '../../services/operating-expense.service';
import { SupplierService } from '../../services/supplier.service';
import { LocalMachineService } from '../../services/local-machine.service';
import { Supplier, LocalMachine } from '../../models/models';
import { ObjectUrlCache } from '../shared/object-url-cache';
import { IconComponent } from '../shared/icon.component';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { OPERATING_EXPENSE_CATEGORIES, expenseCategoryLabel } from './expense-categories';
import {
  ExpenseRegisterFilters,
  expenseFilterQueryParams,
  readExpenseFilters,
  sameExpenseFilterParams
} from './expense-register-filters';

/**
 * Operating Expenses: the manual expenses a business records itself.
 *
 * This is the **register**. Since issue #1167 it records nothing itself: Add Expense and each row's
 * Edit open the dedicated `/expenses/new` and `/expenses/:id/edit` pages, and the card this page used
 * to open above itself is gone. What is stored, calculated or attached is unchanged - those pages
 * make the same two requests against the same endpoints.
 *
 * **The applied filters live in the address.** `from`, `to`, `category`, `supplierId` and `machineId`
 * are query parameters: Apply filters writes them, opening `/expenses` with them applies them, and
 * Add and Edit carry them so a save, a Cancel or Back to expenses returns to the register the
 * operator left. A parameter carrying a value this application cannot use is ignored, not refused.
 *
 * **Nothing on this page needs Nayax to work, and without Nayax it stops mentioning it** (issue
 * #728, decision P9 of issue #329).
 *
 * - The list's **machine filter** narrows the expenses to one of the business's **own** machine
 *   records through the `machineId` parameter `GET /api/operating-expenses` has accepted since issue
 *   #780 - the same local id, so no contract changes. It lists the **deactivated** machines too,
 *   because an expense incurred on a machine that has since been retired is still a cost that was
 *   incurred and has to stay findable; see `machineFilterShown`. A read that **failed** reports
 *   itself where the filter was and one still outstanding says it is loading; neither is ever
 *   presented as "no machines".
 * - The **"Nayax monthly fee"** category stays in the filter whether or not Nayax is part of the
 *   business, because recorded expenses still carry it, and the subtitle drops the Nayax sentence
 *   without it. Whether a **new** expense may be recorded under it is the form's rule
 *   (`expense-form.component.ts`).
 * - The **supporting document** is protected by the API, so each row's View fetches it through
 *   `HttpClient` and opens it from a temporary object URL.
 */
@Component({
  selector: 'app-operating-expense',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, IconComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Operating Expenses</h1>
          <p class="page-subtitle" data-testid="expenses-subtitle">{{ subtitle }}</p>
        </div>
        <div class="page-actions">
          @if (!sessionActive) {
            <!-- The applied filters travel with the link, so Save, Cancel and Back come back here. -->
            <a
              routerLink="/expenses/new"
              [queryParams]="returnQueryParams"
              class="btn btn-primary"
              data-testid="expenses-add"
            >
              <app-icon name="add" [size]="16" />
              Add Expense
            </a>
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
            @if (machinesError) {
              <div class="field">
                <span class="field-label">Machine</span>
                <p data-testid="machine-filter-error" class="text-md-body text-md-danger-text">{{ machinesError }}</p>
              </div>
            } @else if (machinesLoading) {
              <div class="field">
                <span class="field-label">Machine</span>
                <output data-testid="machine-filter-loading" class="field-hint">Loading machines…</output>
              </div>
            } @else if (machineFilterShown) {
              <label class="field">
                <span class="field-label">Machine</span>
                <select id="expense-machine-filter" [(ngModel)]="filters.machineId">
                  <option [ngValue]="undefined">All machines</option>
                  <!-- Every recorded machine, deactivated ones included, so a retired machine's expenses stay findable. -->
                  @for (machine of machines; track machine.id) {
                    <option [ngValue]="machine.id">{{ machine.name }}</option>
                  }
                </select>
              </label>
            }
          </div>
          <button type="button" class="btn btn-secondary btn-sm mt-3" (click)="applyFilters()">Apply filters</button>
        </div>
      </section>

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
                          <a
                            [routerLink]="['/expenses', expense.id, 'edit']"
                            [queryParams]="returnQueryParams"
                            class="btn btn-sm btn-secondary"
                            data-testid="expenses-edit"
                          >
                            <app-icon name="edit" [size]="16" />
                            Edit
                          </a>
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
  readonly categories = OPERATING_EXPENSE_CATEGORIES;
  expenses: OperatingExpense[] = []; suppliers: Supplier[] = [];
  /** Every machine this business has recorded, active and inactive alike (issue #746). */
  machines: LocalMachine[] = [];
  /** The filter controls on screen, which become the applied filters when Apply is pressed. */
  filters: ExpenseRegisterFilters = {};
  /**
   * The filters the list on screen was read with - the ones in the address. Add and Edit are linked
   * with these rather than with {@link filters}, so an edit made halfway through changing a filter
   * returns to the register the operator is actually looking at.
   */
  appliedFilters: ExpenseRegisterFilters = {};
  /**
   * Whether a support session is active (issue #659, extending #554): Add/Edit/Delete hide while it
   * is, because a session grants `ExpensesManage` - the capability this page would otherwise gate
   * those writes on - and the backend refuses the write itself only by its read-only method rule,
   * never by withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;
  /** Set when the machine list could not be read at all; never shown as "no machines". */
  machinesError = '';
  /** Whether that read is still outstanding, which is not the same as there being no machines. */
  machinesLoading = false;
  /** Whether Nayax is part of this business at all; the subtitle's Nayax sentence needs it. */
  providerRecordsAvailable = true;
  // Supporting documents are protected by the API, so they are fetched through HttpClient
  // (which attaches the bearer token) and opened from a temporary object URL.
  private readonly objectUrls = new ObjectUrlCache();
  private readonly subscriptions = new Subscription();
  constructor(
    public service: OperatingExpenseService,
    private supplierService: SupplierService,
    private localMachineService: LocalMachineService,
    private provider: ProviderAvailabilityService,
    private router: Router,
    private route: ActivatedRoute,
    supportSession: SupportSessionService
  ) {
    this.subscriptions.add(supportSession.active$.subscribe((active) => (this.sessionActive = active)));
  }
  ngOnInit(): void {
    this.subscriptions.add(this.supplierService.getAll().subscribe(x => this.suppliers = x));
    this.loadMachines();
    this.subscriptions.add(
      this.provider.featureOffered('providerRecords').subscribe(available => (this.providerRecordsAvailable = available))
    );
    // The address is what the list is read from, so a link, a bookmark, a reload and the return from
    // Add or Edit all show the same filtered register.
    this.subscriptions.add(
      this.route.queryParamMap.subscribe(params => {
        this.filters = readExpenseFilters(params);
        this.appliedFilters = { ...this.filters };
        this.load();
      })
    );
  }

  /** The applied filters as the query parameters Add and Edit carry, and come back with. */
  get returnQueryParams(): Params {
    return expenseFilterQueryParams(this.appliedFilters);
  }

  /**
   * Apply filters: the chosen filters become the address, which is what reads the list. Pressing it
   * without having changed anything names the address already shown, which the router would ignore,
   * so the list is re-read directly - Apply has always been a way to ask the server again.
   */
  applyFilters(): void {
    const requested = expenseFilterQueryParams(this.filters);
    if (sameExpenseFilterParams(requested, expenseFilterQueryParams(this.appliedFilters))) {
      this.load();
      return;
    }
    // The applied filters are set by the address this navigation produces, never ahead of it, so a
    // navigation that does not happen cannot leave the links claiming a register nobody is looking at.
    this.router.navigate(['/expenses'], { queryParams: requested });
  }

  private loadMachines(): void {
    this.machinesLoading = true;
    this.machinesError = '';
    this.subscriptions.add(
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
      })
    );
  }

  /** The page's own wording: without Nayax it describes only what this business does record. */
  get subtitle(): string {
    return this.providerRecordsAvailable
      ? 'Manual expenses only. Nayax processing, commissions, delivery and packaging remain separately sourced.'
      : 'Manual expenses only. Commissions, delivery and packaging remain separately sourced.';
  }

  /**
   * Whether the list's machine filter is offered at all: only once the business has an **active**
   * machine to filter by (issue #746), the same rule the Stock History filter uses. A failed read is
   * reported in its place ({@link machinesError}) and an outstanding one says it is loading, so an
   * empty filter is never a statement about a business whose machines nobody read. The filter itself
   * lists every recorded machine, deactivated ones included: an expense attributed to a machine that
   * has since been retired is still a cost that was incurred, and it has to stay findable.
   */
  get machineFilterShown(): boolean {
    return this.machinesError === '' && !this.machinesLoading && this.machines.some(machine => machine.isActive);
  }
  ngOnDestroy(): void { this.objectUrls.releaseAll(); this.subscriptions.unsubscribe(); }
  viewAttachment(expense: OperatingExpense): void {
    this.service.getAttachment(expense.id).subscribe({
      next: blob => window.open(this.objectUrls.create(blob), '_blank', 'noopener'),
      error: err => console.error('Failed to open supporting document', err)
    });
  }
  load(): void { this.service.getAll(this.appliedFilters).subscribe(x => this.expenses = x); }
  // Checked here as well as hidden in the template, so the write is actually unavailable rather
  // than merely invisible - the endpoint refuses it either way.
  remove(expense: OperatingExpense): void { if (!this.sessionActive && confirm(`Delete expense "${expense.description}"?`)) this.service.delete(expense.id).subscribe(() => this.load()); }
  categoryLabel(category: OperatingExpenseCategory): string { return expenseCategoryLabel(category); }
  total(field: 'amountExGst' | 'gstAmount' | 'totalAmount'): number { return this.expenses.reduce((sum, item) => sum + Number(item[field] || 0), 0); }
}
