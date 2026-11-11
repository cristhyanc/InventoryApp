import { Component, ElementRef, OnDestroy, OnInit } from '@angular/core';
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
import { ActionMenuComponent } from '../shared/action-menu/action-menu.component';
import { ListPaginationComponent } from '../shared/list-pagination/list-pagination.component';
import { clampPageIndex, pageSlice } from '../shared/list-pagination/paging';
import { ListLoadState } from '../shared/list-load-state';
import { apiErrorMessage } from '../shared/api-error-message';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { OPERATING_EXPENSE_CATEGORIES, expenseCategoryLabel } from './expense-categories';
import { ExpenseDeleteConfirmationComponent } from './expense-delete-confirmation/expense-delete-confirmation.component';
import {
  EXPENSE_DEFAULT_PAGE_SIZE,
  EXPENSE_FILTER_PARAM_NAMES,
  EXPENSE_PAGE_SIZE_OPTIONS,
  ExpenseRegisterFilters,
  expenseFilterQueryParams,
  expenseRegisterQueryParams,
  readExpenseRegisterView,
  sameExpenseFilterParams,
  sameExpenseRegisterParams
} from './expense-register-filters';

/**
 * Operating Expenses: the manual expenses a business records itself.
 *
 * This is the **register**. Since issue #1167 it records nothing itself: Add expense and each row's
 * Edit open the dedicated `/expenses/new` and `/expenses/:id/edit` pages. Issue #1170 redesigned it
 * on the Products list's layout - a compact toolbar, a nine-column table above `md` and the same
 * expenses as cards below it, a client-side pager, and a confirmation for Delete.
 *
 * **The register's filters and its page live in the address.** `from`, `to`, `category`,
 * `supplierId`, `machineId`, `page` and `pageSize` are query parameters (parsed in one place,
 * `expense-register-filters.ts`): Apply filters writes the filters, turning a page writes the page,
 * opening `/expenses` with them opens exactly that register, and Add and Edit carry them so a save,
 * a Cancel or Back to expenses returns to where the operator was. A parameter carrying a value this
 * application cannot use is ignored, not refused.
 *
 * **Paging is client-side over the one list already read** (issue #1170, as on Products): the API
 * answers the whole filtered list, so turning a page or changing the size requests nothing, the
 * **Totals** footer sums every filtered row rather than the page on screen, and the pager's counts
 * are the real filtered counts.
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
 *   presented as "no machines". Supplier and Machine sit behind **More filters**, which opens by
 *   itself when the address already applies one of them.
 * - The **"Nayax monthly fee"** category stays in the filter whether or not Nayax is part of the
 *   business, because recorded expenses still carry it, and the subtitle drops the Nayax sentence
 *   without it. Whether a **new** expense may be recorded under it is the form's rule
 *   (`expense-form.component.ts`).
 * - The **supporting document** is protected by the API, so each row's View fetches it through
 *   `HttpClient` and opens it from a temporary object URL. A read that fails says so on the page.
 */
@Component({
  selector: 'app-operating-expense',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    IconComponent,
    ActionMenuComponent,
    ListPaginationComponent,
    ExpenseDeleteConfirmationComponent
  ],
  templateUrl: './operating-expense.component.html'
})
export class OperatingExpenseComponent implements OnInit, OnDestroy {
  readonly categories = OPERATING_EXPENSE_CATEGORIES;
  /** The whole filtered list the API answered; the page on screen is a window onto it. */
  expenses: OperatingExpense[] = [];
  suppliers: Supplier[] = [];
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
  /** Whether the More filters disclosure (Supplier and Machine) is open. */
  moreFiltersOpen = false;

  /** Where the list read stands, so a failed or outstanding read is never shown as "no expenses". */
  readonly loadState = new ListLoadState();

  /** The page of the filtered list on screen, and its size - both read from the address. */
  pageIndex = 0;
  pageSize: number = EXPENSE_DEFAULT_PAGE_SIZE;
  readonly pageSizeOptions: readonly number[] = EXPENSE_PAGE_SIZE_OPTIONS;

  /** The expense waiting for its delete to be confirmed, or `null` while none is. */
  confirmingExpense: OperatingExpense | null = null;

  /**
   * What a failed delete or a failed document read said. It is shown on the page rather than only
   * logged, and a failure never looks like a success: the row stays exactly as it was fetched.
   */
  actionError = '';

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
  /** Whether the address has been read once, so the first emission always reads the list. */
  private addressRead = false;

  constructor(
    public service: OperatingExpenseService,
    private supplierService: SupplierService,
    private localMachineService: LocalMachineService,
    private provider: ProviderAvailabilityService,
    private router: Router,
    private route: ActivatedRoute,
    private host: ElementRef<HTMLElement>,
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
    // The address is what the register is read from and which page of it is shown, so a link, a
    // bookmark, a reload and the return from Add or Edit all show the same page of the same list.
    this.subscriptions.add(
      this.route.queryParamMap.subscribe(params => {
        const view = readExpenseRegisterView(params);
        const filtersChanged =
          !this.addressRead ||
          !sameExpenseFilterParams(expenseFilterQueryParams(view.filters), expenseFilterQueryParams(this.appliedFilters));

        this.filters = { ...view.filters };
        this.appliedFilters = { ...view.filters };
        this.pageIndex = view.pageIndex;
        this.pageSize = view.pageSize;
        if (this.advancedFilterApplied) {
          this.moreFiltersOpen = true;
        }

        if (filtersChanged) {
          this.addressRead = true;
          this.load();
          return;
        }
        // Only the page changed: the list on screen is the one already read, so nothing is
        // requested - but a page past the end of it still has to settle onto a page that exists.
        this.settlePage();
      })
    );
  }

  /** The applied filters and page as the query parameters Add and Edit carry, and come back with. */
  get returnQueryParams(): Params {
    return expenseRegisterQueryParams({
      filters: this.appliedFilters,
      pageIndex: this.pageIndex,
      pageSize: this.pageSize
    });
  }

  /** How many filters the register is actually applying, which is what the toolbar reports. */
  get appliedFilterCount(): number {
    const applied = expenseFilterQueryParams(this.appliedFilters);
    return EXPENSE_FILTER_PARAM_NAMES.filter((name) => applied[name] !== undefined).length;
  }

  /**
   * Whether Clear filters is offered: with nothing applied **and** nothing chosen it would be a
   * no-op, so it appears as soon as there is something to clear.
   */
  get clearFiltersOffered(): boolean {
    return this.appliedFilterCount > 0 || Object.keys(expenseFilterQueryParams(this.filters)).length > 0;
  }

  /** Whether the address applies one of the filters More filters holds, which is why it opens. */
  private get advancedFilterApplied(): boolean {
    return this.appliedFilters.supplierId !== undefined || this.appliedFilters.machineId !== undefined;
  }

  toggleMoreFilters(): void {
    this.moreFiltersOpen = !this.moreFiltersOpen;
  }

  /** The expenses on the current page - one list both the table and the cards render. */
  get pagedExpenses(): OperatingExpense[] {
    return pageSlice(this.expenses, this.pageIndex, this.pageSize);
  }

  /** What the Totals row says it covers, so a reader is never left thinking it is the page. */
  get totalsLabel(): string {
    return this.expenses.length === 1
      ? 'Totals (the 1 filtered expense)'
      : `Totals (all ${this.expenses.length} filtered expenses)`;
  }

  /**
   * Apply filters: the chosen filters become the address, which is what reads the list, and the
   * register returns to page 1 because a new filter is a new list. Pressing it without having
   * changed anything names the register already shown, which the router would ignore, so the list
   * is re-read directly - Apply has always been a way to ask the server again.
   */
  applyFilters(): void {
    const requested = expenseRegisterQueryParams({ filters: this.filters, pageIndex: 0, pageSize: this.pageSize });
    if (sameExpenseRegisterParams(requested, this.returnQueryParams)) {
      this.load();
      return;
    }
    // The applied filters are set by the address this navigation produces, never ahead of it, so a
    // navigation that does not happen cannot leave the links claiming a register nobody is looking at.
    this.router.navigate(['/expenses'], { queryParams: requested });
  }

  /**
   * Clear filters: back to no filters at all, which is this register's default, and to page 1. The
   * page **size** is a view preference rather than a filter, so it is kept. With nothing applied
   * there is no address to change and only the controls are reset.
   */
  clearFilters(): void {
    this.filters = {};
    const requested = expenseRegisterQueryParams({ filters: {}, pageIndex: 0, pageSize: this.pageSize });
    if (sameExpenseRegisterParams(requested, this.returnQueryParams)) {
      return;
    }
    this.router.navigate(['/expenses'], { queryParams: requested });
  }

  /** Turning a page: the page is in the address, and the list it slices is the one already read. */
  onPageIndexChange(index: number): void {
    this.goToPage(clampPageIndex(index, this.expenses.length, this.pageSize), false);
  }

  /** A new page size is a fresh view of the same list, so it returns to page 1. */
  onPageSizeChange(size: number): void {
    if (!this.pageSizeOptions.includes(size) || size === this.pageSize) {
      return;
    }
    this.pageSize = size;
    this.goToPage(0, false);
  }

  /**
   * Moves to a page of the list already read: the page is set here and written to the address, so
   * the register on screen and the address can never disagree. Nothing is requested.
   */
  private goToPage(index: number, replaceUrl: boolean): void {
    this.pageIndex = index;
    this.router.navigate(['/expenses'], { queryParams: this.returnQueryParams, replaceUrl });
  }

  /**
   * Settles the page onto one the loaded list actually has. It is what steps back a page when the
   * last row of a page is deleted, and what a stale bookmarked `page` lands on; the address is
   * rewritten in place rather than pushed, because nobody asked to go anywhere.
   */
  private settlePage(): void {
    if (!this.loadState.loaded) {
      return;
    }
    const settled = clampPageIndex(this.pageIndex, this.expenses.length, this.pageSize);
    if (settled !== this.pageIndex) {
      this.goToPage(settled, true);
    }
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
      next: blob => {
        this.actionError = '';
        window.open(this.objectUrls.create(blob), '_blank', 'noopener');
      },
      error: error => {
        this.actionError = apiErrorMessage(
          error,
          `The document for "${expense.description}" could not be opened. Try again, or open the expense to check the document.`
        );
      }
    });
  }

  /** Reads the filtered list the address asks for. The page it shows is settled once it answers. */
  load(): void {
    const token = this.loadState.start();
    this.service.getAll(this.appliedFilters).subscribe({
      next: expenses => {
        if (this.loadState.isCurrent(token)) {
          this.expenses = expenses ?? [];
        }
        this.loadState.succeed(token);
        this.settlePage();
      },
      error: () => this.loadState.fail(token)
    });
  }

  /**
   * Opens the confirmation for one expense. Opening the row menu, and choosing nothing in it,
   * deletes nothing: this is the only way to a delete, and it still only asks.
   *
   * Checked here as well as hidden in the template, so the write is actually unavailable rather
   * than merely invisible during a support session - the endpoint refuses it either way.
   */
  confirmDelete(expense: OperatingExpense): void {
    if (this.sessionActive) {
      return;
    }
    this.confirmingExpense = expense;
  }

  cancelDelete(): void {
    this.confirmingExpense = null;
  }

  /**
   * The confirmed delete. On success the register is re-read with the same filters and stays on the
   * same page, stepping back one when the page it was on no longer exists; on a refusal the row
   * stays exactly as it was and the refusal is shown, never a success.
   */
  deleteConfirmed(expense: OperatingExpense): void {
    this.confirmingExpense = null;
    if (this.sessionActive) {
      return;
    }

    this.service.delete(expense.id).subscribe({
      next: () => {
        this.actionError = '';
        this.load();
        // The row the menu belonged to has gone with the expense, so focus moves to the one control
        // that is always there (Add expense) rather than being dropped on the document.
        this.host.nativeElement.querySelector<HTMLElement>('[data-testid="expenses-add"]')?.focus();
      },
      error: error =>
        (this.actionError = apiErrorMessage(
          error,
          `"${expense.description}" could not be deleted, so it is still recorded.`
        ))
    });
  }

  dismissActionError(): void {
    this.actionError = '';
  }

  categoryLabel(category: OperatingExpenseCategory): string { return expenseCategoryLabel(category); }

  /** The totals cover every filtered row, not only the page on screen. */
  total(field: 'amountExGst' | 'gstAmount' | 'totalAmount'): number {
    return this.expenses.reduce((sum, item) => sum + Number(item[field] || 0), 0);
  }
}
