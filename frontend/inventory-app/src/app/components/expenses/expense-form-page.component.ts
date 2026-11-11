import { Component, ElementRef, HostListener, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, Params, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { LocalMachine, Supplier } from '../../models/models';
import {
  OperatingExpenseDetail,
  OperatingExpenseService
} from '../../services/operating-expense.service';
import { SupplierService } from '../../services/supplier.service';
import { LocalMachineService } from '../../services/local-machine.service';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import { apiErrorMessage } from '../shared/api-error-message';
import { ObjectUrlCache } from '../shared/object-url-cache';
import { PendingWorkGuarded } from '../pick-list/pick-list-leave.guard';
import {
  ExpenseFormComponent,
  ExpenseFormValue,
  ExpenseSaveRequest,
  newExpenseFormValue
} from './expense-form.component';
import { expenseFilterQueryParams, readExpenseFilters } from './expense-register-filters';

/** The one sentence a departure asks while there is unsaved work (issue #1167). */
export const EXPENSE_UNSAVED_LEAVE_WARNING =
  'You have unsaved changes to this expense. Leave this page and discard them?';

/** What a network failure says: the expense was not saved, whatever else is unknown. */
export const EXPENSE_NETWORK_SAVE_FAILURE =
  'The expense was not saved. Check your connection and try again.';

/**
 * The one notice both pages show while a support session is active (issue #1167). It is deliberately
 * a single shared sentence rather than one worded per page: Add and Edit refuse the same thing - any
 * write to an expense from outside the business - and the issue requires the same note on both.
 */
export const EXPENSE_SUPPORT_SESSION_NOTE = "Expenses can't be added during a support session.";

/**
 * Add expense and Edit expense (issue #1167), reached at `/expenses/new` and `/expenses/:id/edit`.
 *
 * Recording an expense used to be a card the `/expenses` register opened above itself; these are the
 * dedicated pages that replaced it, on the Restockroo layout New/Edit Product established (issue
 * #1111). **Nothing about how an expense is stored, calculated or attached changes**: the same two
 * requests are made, with the same payload, against the same endpoints.
 *
 * This is the composition boundary described in `docs/architecture.md` § Page composition boundary.
 * The page resolves the route, loads the expense through `OperatingExpenseService.getById` (so a
 * bookmarked URL and a reload both work without any register instance or navigation-only state),
 * loads the suppliers, the business's own machines and whether Nayax is part of the business, owns
 * the loading / not-found / load-failure / saving / refusal states and the save request itself, and
 * navigates back. {@link ExpenseFormComponent} is the form both pages share.
 *
 * **The register's filters are the return context.** They travel as the query parameters `from`,
 * `to`, `category`, `supplierId` and `machineId`, and a successful save, Cancel and Back to expenses
 * all return to `/expenses` carrying them, so the operator comes back to the register they left. A
 * parameter with a value this application cannot use is ignored rather than refused.
 *
 * **A refused save keeps the operator's work.** The page stays open with every entered value and the
 * chosen document intact and shows the server's own sentence, with focus moved to it. A `409` - the
 * attachment replacement race the API answers for an expense somebody else attached to first - also
 * offers to reload the expense, because the only way to replace a document that has moved on is to
 * see what is stored now.
 *
 * Permissions and tenant isolation are the API's, unchanged: the expense endpoints are authorized
 * and scoped by the `AppDbContext` tenant filters, so another business's id reaches this page's
 * not-found state rather than its data. During a support session the capability guard still passes -
 * a session grants `Expenses.Manage` - so this page refuses the write itself: Add shows no form at
 * all, and Edit shows the expense read-only.
 */
@Component({
  selector: 'app-expense-form-page',
  standalone: true,
  imports: [CommonModule, RouterLink, ExpenseFormComponent],
  templateUrl: './expense-form-page.component.html'
})
export class ExpenseFormPageComponent implements OnInit, OnDestroy, PendingWorkGuarded {
  /** The expense being edited, or `null` on Add expense. */
  expenseId: number | null = null;

  /** The values the form opens with, or `null` while the expense has not been read. */
  value: ExpenseFormValue | null = null;

  loading = false;

  /** Whether the address names an expense this business cannot see - or no expense at all. */
  notFound = false;

  /** Set when the read failed for any other reason, so Retry is worth offering. */
  loadError = '';

  saving = false;

  /** The server's own sentence for the save it refused, or the page's own for a failed request. */
  error = '';

  /** Whether that refusal was the attachment replacement race, which offers a reload as well. */
  conflict = false;

  suppliers: Supplier[] = [];

  /** Every machine this business has recorded, active and inactive alike (issue #746). */
  machines: LocalMachine[] = [];
  machinesError = '';
  machinesLoading = false;

  /** Whether Nayax is part of this business at all; the Nayax fee category needs it. */
  providerRecordsAvailable = true;

  /**
   * Whether a support session is active (issue #659). A session grants `Expenses.Manage`, the
   * capability this page is gated on, and the backend refuses the write only by its read-only method
   * rule - so the page refuses it too rather than offering a form that cannot save.
   */
  sessionActive = false;

  /** The register's applied filters, carried back on a save, a Cancel and Back to expenses. */
  returnQueryParams: Params = {};

  @ViewChild(ExpenseFormComponent) private readonly form?: ExpenseFormComponent;

  /** Set once the save succeeded, so the navigation back to the register asks nothing. */
  private leaving = false;

  /** Whether the next render of the page-level error should take focus (one failed save, once). */
  private focusErrorOnRender = false;

  /**
   * Which load of the page a callback belongs to. Every load and the page's destruction move it on,
   * so a response for an expense the operator has left cannot touch the one now on screen.
   */
  private context = 0;

  private readonly subscriptions = new Subscription();

  // The supporting document is protected by the API, so it is fetched through HttpClient (which
  // attaches the bearer token) and opened from a temporary object URL.
  private readonly objectUrls = new ObjectUrlCache();

  constructor(
    private readonly service: OperatingExpenseService,
    private readonly supplierService: SupplierService,
    private readonly localMachineService: LocalMachineService,
    private readonly provider: ProviderAvailabilityService,
    private readonly router: Router,
    private readonly route: ActivatedRoute,
    private readonly businessTimeZone: BusinessTimeZoneService,
    supportSession: SupportSessionService
  ) {
    this.subscriptions.add(supportSession.active$.subscribe((active) => (this.sessionActive = active)));
  }

  ngOnInit(): void {
    this.subscriptions.add(this.supplierService.getAll().subscribe((suppliers) => (this.suppliers = suppliers)));
    this.subscriptions.add(
      this.provider.featureOffered('providerRecords').subscribe((available) => (this.providerRecordsAvailable = available))
    );
    this.loadMachines();

    // The applied filters travel in the address, so a reload of this page keeps the return context.
    this.subscriptions.add(
      this.route.queryParamMap.subscribe((params) => (this.returnQueryParams = expenseFilterQueryParams(readExpenseFilters(params))))
    );
    // paramMap rather than a snapshot: the router reuses this component between two edit URLs, so
    // browser Back and Forward must load the expense the current address names.
    this.subscriptions.add(this.route.paramMap.subscribe((params) => this.load(params.get('id'))));
  }

  ngOnDestroy(): void {
    this.context++;
    this.subscriptions.unsubscribe();
    this.objectUrls.releaseAll();
  }

  /** Whether this page is adding an expense rather than editing one. */
  get creating(): boolean {
    return this.expenseId === null;
  }

  get heading(): string {
    return this.creating ? 'Add expense' : 'Edit expense';
  }

  /**
   * Why no form is shown, or none may be saved, during a support session: the same note on both
   * pages, as issue #1167 requires. Adding is refused outright; an existing expense is still shown,
   * read-only, because reading it is not a write.
   */
  readonly supportSessionNote = EXPENSE_SUPPORT_SESSION_NOTE;

  /** Whether the whole form is withheld: only Add expense during a support session. */
  get addBlockedBySession(): boolean {
    return this.sessionActive && this.creating;
  }

  /** The press of the form's primary action: the one create or update this page makes. */
  onSave(request: ExpenseSaveRequest): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive || this.saving) {
      return;
    }

    this.saving = true;
    this.error = '';
    this.conflict = false;

    const context = this.context;
    const expenseId = this.expenseId;
    const save = expenseId === null
      ? this.service.create(request.payload, request.attachment)
      : this.service.update(expenseId, request.payload, request.attachment);

    save.subscribe({
      next: () => {
        if (context !== this.context) {
          return;
        }
        this.saving = false;
        this.leave();
      },
      error: (failure: unknown) => {
        if (context !== this.context) {
          return;
        }
        this.saving = false;
        // Every value and the chosen file stay on the form: the page under the refusal is the one
        // the operator submitted, so a second press retries exactly what they entered.
        this.conflict = failure instanceof HttpErrorResponse && failure.status === 409;
        this.error = this.saveFailureMessage(failure);
        this.focusErrorOnRender = true;
      }
    });
  }

  /** Retry after a failed read, and the reload a conflicting save offers: the same read again. */
  reload(): void {
    this.load(this.expenseId === null ? null : String(this.expenseId));
  }

  /** Opens the expense's stored supporting document, fetched through the authorized API. */
  viewDocument(): void {
    if (this.expenseId === null) {
      return;
    }
    const context = this.context;
    this.service.getAttachment(this.expenseId).subscribe({
      next: (blob) => {
        if (context === this.context) {
          window.open(this.objectUrls.create(blob), '_blank', 'noopener');
        }
      },
      error: (failure: unknown) => console.error('Failed to open supporting document', failure)
    });
  }

  /** The departure warning that applies right now, or `null` when leaving loses nothing. */
  leaveWarning(): string | null {
    if (this.leaving) {
      return null;
    }
    return this.form?.dirty ? EXPENSE_UNSAVED_LEAVE_WARNING : null;
  }

  /** The route's `canDeactivate` answer: leave freely, or only once the operator confirms. */
  confirmLeave(): boolean {
    const warning = this.leaveWarning();
    return warning === null || window.confirm(warning);
  }

  /** Asks the browser to confirm a reload or a closed tab while there is work to lose. */
  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.leaveWarning() !== null) {
      event.preventDefault();
    }
  }

  /**
   * Moves focus to the page-level error the moment it appears, so a refused save is announced to a
   * screen reader and is where the keyboard already is rather than above the viewport.
   */
  @ViewChild('pageError') set pageError(element: ElementRef<HTMLElement> | undefined) {
    if (element && this.focusErrorOnRender) {
      this.focusErrorOnRender = false;
      element.nativeElement.focus();
    }
  }

  private load(idParam: string | null): void {
    this.context++;
    const context = this.context;
    this.value = null;
    this.saving = false;
    this.error = '';
    this.conflict = false;
    this.notFound = false;
    this.loadError = '';
    this.leaving = false;

    if (idParam === null) {
      // Add expense: there is nothing to read, so the form opens on a brand-new expense.
      this.expenseId = null;
      this.loading = false;
      this.value = newExpenseFormValue(this.businessTimeZone.timeZoneId);
      return;
    }

    const id = Number(idParam);
    if (!/^\d+$/.test(idParam) || !Number.isInteger(id) || id <= 0) {
      // An address that names no expense at all is the same answer as one naming a missing expense:
      // there is nothing to edit, and the API's own route would not match it either.
      this.expenseId = null;
      this.loading = false;
      this.notFound = true;
      return;
    }

    this.expenseId = id;
    this.loading = true;
    this.service.getById(id).subscribe({
      next: (expense) => {
        if (context !== this.context) {
          return;
        }
        this.loading = false;
        this.value = toFormValue(expense);
      },
      error: (failure: unknown) => {
        if (context !== this.context) {
          return;
        }
        this.loading = false;
        // A `404` is both a missing expense and one belonging to another business: either way there
        // is nothing to edit, and saying which would answer a question about another tenant.
        if (failure instanceof HttpErrorResponse && failure.status === 404) {
          this.notFound = true;
          return;
        }
        this.loadError = 'This expense could not be loaded.';
        console.error('Failed to load operating expense', failure);
      }
    });
  }

  private loadMachines(): void {
    this.machinesLoading = true;
    this.machinesError = '';
    this.subscriptions.add(
      this.localMachineService.getAll().subscribe({
        next: (machines) => {
          this.machines = machines ?? [];
          this.machinesLoading = false;
        },
        error: () => {
          this.machines = [];
          this.machinesError =
            'The machine list could not be loaded, so an expense cannot be attributed to a machine.';
          this.machinesLoading = false;
        }
      })
    );
  }

  /** The sentence a refused or failed save shows: the server's own wherever it sent one. */
  private saveFailureMessage(failure: unknown): string {
    if (failure instanceof HttpErrorResponse && failure.status === 0) {
      return EXPENSE_NETWORK_SAVE_FAILURE;
    }
    return apiErrorMessage(failure, EXPENSE_NETWORK_SAVE_FAILURE);
  }

  private leave(): void {
    this.leaving = true;
    this.router.navigate(['/expenses'], { queryParams: this.returnQueryParams });
  }
}

/**
 * The loaded expense as the form opens it. `GET /api/operating-expenses/{id}` nests the whole
 * `supplier` object rather than flattening it, so the id the form submits is read from the response's
 * own `supplierId`; the date is cut to the calendar date the date input holds; and `siteId`,
 * `servicePeriodStart` and `servicePeriodEnd` are carried through untouched, because this form has no
 * control for them and must not clear what it cannot show.
 */
function toFormValue(expense: OperatingExpenseDetail): ExpenseFormValue {
  return {
    payload: {
      expenseDate: expense.expenseDate.substring(0, 10),
      category: expense.category,
      description: expense.description,
      amountExGst: expense.amountExGst,
      gstAmount: expense.gstAmount,
      totalAmount: expense.totalAmount,
      supplierId: expense.supplierId ?? expense.supplier?.id ?? null,
      siteId: expense.siteId ?? null,
      machineId: expense.machineId ?? null,
      servicePeriodStart: expense.servicePeriodStart ?? null,
      servicePeriodEnd: expense.servicePeriodEnd ?? null,
      notes: expense.notes ?? null
    },
    recordedMachineId: expense.machineId ?? null,
    attachmentFileName: expense.attachmentFileName ?? null
  };
}
