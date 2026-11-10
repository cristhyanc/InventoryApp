import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, NEVER, Observable, of, throwError } from 'rxjs';
import { LocalMachine } from '../../models/models';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';
import { LocalMachineService } from '../../services/local-machine.service';
import {
  OperatingExpenseCategory,
  OperatingExpenseDetail,
  OperatingExpenseService
} from '../../services/operating-expense.service';
import { SupplierService } from '../../services/supplier.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import {
  EXPENSE_SUPPORT_SESSION_NOTE,
  EXPENSE_UNSAVED_LEAVE_WARNING,
  ExpenseFormPageComponent
} from './expense-form-page.component';
import { ExpenseFormComponent } from './expense-form.component';

/** The single-expense read's own shape: a nested `supplier`, and the two timestamps. */
function detail(overrides: Partial<OperatingExpenseDetail> = {}): OperatingExpenseDetail {
  return {
    id: 7,
    expenseDate: '2026-01-15T00:00:00Z',
    category: OperatingExpenseCategory.Insurance,
    description: 'Annual policy',
    amountExGst: 100,
    gstAmount: 10,
    totalAmount: 110,
    supplierId: 3,
    supplier: { id: 3, name: 'Acme Insurance' },
    siteId: 12,
    machineId: 41,
    servicePeriodStart: '2026-02-01T00:00:00Z',
    servicePeriodEnd: '2027-01-31T00:00:00Z',
    attachmentFileName: null,
    notes: 'Renewed',
    createdAt: '2026-01-15T01:00:00Z',
    updatedAt: '2026-01-15T01:00:00Z',
    ...overrides
  };
}

function localMachine(id: number, name: string, isActive = true): LocalMachine {
  return { id, name, isActive, isProviderNamed: false };
}

async function render(
  options: {
    /** `null` is `/expenses/new`; a string is the `:id` segment of `/expenses/:id/edit`. */
    id?: string | null;
    queryParams?: Record<string, string>;
    getById?: jest.Mock;
    create?: jest.Mock;
    update?: jest.Mock;
    machines?: Observable<LocalMachine[]>;
    sessionActive?: boolean;
  } = {}
) {
  const getById = options.getById ?? jest.fn(() => of(detail()));
  const create = options.create ?? jest.fn(() => of(detail()));
  const update = options.update ?? jest.fn(() => of(detail()));
  const getAttachment = jest.fn(() => of(new Blob(['x'])));
  const paramMap = new BehaviorSubject(
    convertToParamMap(options.id === undefined || options.id === null ? {} : { id: options.id })
  );
  const queryParamMap = new BehaviorSubject(convertToParamMap(options.queryParams ?? {}));

  await TestBed.configureTestingModule({
    imports: [ExpenseFormPageComponent],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { paramMap, queryParamMap } },
      {
        provide: OperatingExpenseService,
        useValue: { getById, create, update, getAttachment, getAll: jest.fn(() => of([])) }
      },
      { provide: SupplierService, useValue: { getAll: jest.fn(() => of([{ id: 3, name: 'Acme Insurance' }])) } },
      {
        provide: LocalMachineService,
        useValue: { getAll: jest.fn(() => options.machines ?? of([localMachine(41, 'Lobby')])) }
      },
      { provide: SupportSessionService, useValue: { active$: of(options.sessionActive ?? false) } },
      provideNayaxConnectionStatus()
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ExpenseFormPageComponent);
  fixture.detectChanges();

  return {
    fixture,
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    getById,
    create,
    update,
    getAttachment,
    paramMap,
    router: TestBed.inject(Router)
  };
}

/** The form child, so a spec can drive the page through the real form rather than around it. */
function form(fixture: ComponentFixture<ExpenseFormPageComponent>): ExpenseFormComponent {
  return fixture.debugElement.query(By.directive(ExpenseFormComponent)).componentInstance;
}

/** Fills in the one field the form requires and presses the primary action. */
function saveWith(fixture: ComponentFixture<ExpenseFormPageComponent>, description = 'Annual policy'): void {
  const child = form(fixture);
  child.form.description = description;
  child.submit();
  fixture.detectChanges();
}

const testId = (host: HTMLElement, id: string) => host.querySelector(`[data-testid="${id}"]`);
const heading = (host: HTMLElement) => host.querySelector('h1')?.textContent?.trim();

afterEach(() => {
  jest.restoreAllMocks();
  TestBed.resetTestingModule();
});

/**
 * Add expense (issue #1167): the page `/expenses/new` serves, which reads nothing and opens the
 * shared form on a brand-new expense.
 */
describe('ExpenseFormPageComponent adding an expense (issue #1167)', () => {
  it('opens the form on a new expense without reading one', async () => {
    const { component, host, getById } = await render({ id: null });

    expect(getById).not.toHaveBeenCalled();
    expect(heading(host)).toBe('Add expense');
    expect(component.creating).toBe(true);
    expect(testId(host, 'expense-form')).not.toBeNull();
    expect(testId(host, 'expense-form-submit')?.textContent?.trim()).toBe('Save expense');
  });

  it('creates the expense and returns to the register', async () => {
    const { fixture, create, router } = await render({ id: null });
    const navigate = jest.spyOn(router, 'navigate').mockResolvedValue(true);

    saveWith(fixture, 'Stationery');

    expect(create).toHaveBeenCalledTimes(1);
    expect(create.mock.calls[0][0]).toMatchObject({ description: 'Stationery', category: OperatingExpenseCategory.Other });
    expect(create.mock.calls[0][1]).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/expenses'], { queryParams: {} });
  });

  it('passes the business-wide reads down to the form', async () => {
    const { fixture } = await render({ id: null });

    expect(form(fixture).suppliers).toEqual([{ id: 3, name: 'Acme Insurance' }]);
    expect(form(fixture).machines).toEqual([localMachine(41, 'Lobby')]);
    expect(form(fixture).providerRecordsAvailable).toBe(true);
  });

  it('reports a failed machine read in the field\'s place rather than as "no machines"', async () => {
    const { fixture, component } = await render({
      id: null,
      machines: throwError(() => new HttpErrorResponse({ status: 500 }))
    });

    expect(component.machinesError).toContain('The machine list could not be loaded');
    expect(form(fixture).machineFieldShown).toBe(false);
    expect(testId(fixture.nativeElement as HTMLElement, 'machine-field-error')).not.toBeNull();
  });

  it('says the machines are still loading rather than offering no field', async () => {
    const { fixture } = await render({ id: null, machines: NEVER });

    expect(testId(fixture.nativeElement as HTMLElement, 'machine-field-loading')).not.toBeNull();
  });
});

/**
 * Edit expense (issue #1167): the page loads the expense the address names through the new
 * single-expense read, so a direct link and a reload both work with no register instance behind them.
 */
describe('ExpenseFormPageComponent loading an expense (issue #1167)', () => {
  it('reads the expense named in the address and fills the form in from it', async () => {
    const { fixture, host, getById } = await render({ id: '7' });

    expect(getById).toHaveBeenCalledWith(7);
    expect(heading(host)).toBe('Edit expense');
    expect(form(fixture).form).toMatchObject({
      expenseDate: '2026-01-15',
      category: OperatingExpenseCategory.Insurance,
      description: 'Annual policy',
      supplierId: 3,
      siteId: 12,
      machineId: 41,
      servicePeriodStart: '2026-02-01T00:00:00Z',
      servicePeriodEnd: '2027-01-31T00:00:00Z',
      notes: 'Renewed'
    });
    expect(testId(host, 'expense-form-submit')?.textContent?.trim()).toBe('Save changes');
  });

  it('shows a loading state while the read is outstanding', async () => {
    const { host } = await render({ id: '7', getById: jest.fn(() => NEVER) });

    expect(testId(host, 'expense-form-loading')).not.toBeNull();
    expect(testId(host, 'expense-form')).toBeNull();
  });

  it('says a missing expense was not found, with a way back and no form', async () => {
    const { host } = await render({
      id: '7',
      getById: jest.fn(() => throwError(() => new HttpErrorResponse({ status: 404 })))
    });

    expect(testId(host, 'expense-form-not-found')?.textContent).toContain('This expense was not found.');
    expect(testId(host, 'expense-form-not-found-back')?.textContent?.trim()).toBe('Back to expenses');
    expect(testId(host, 'expense-form')).toBeNull();
    expect(testId(host, 'expense-form-retry')).toBeNull();
  });

  it("treats another business's expense as not found, because that is all the API tells a client", async () => {
    const { component } = await render({
      id: '4242',
      getById: jest.fn(() => throwError(() => new HttpErrorResponse({ status: 404 })))
    });

    expect(component.notFound).toBe(true);
    expect(component.loadError).toBe('');
  });

  it('treats a non-numeric id as not found, without reading anything', async () => {
    const { host, getById } = await render({ id: 'seven' });

    expect(getById).not.toHaveBeenCalled();
    expect(testId(host, 'expense-form-not-found')).not.toBeNull();
    expect(testId(host, 'expense-form')).toBeNull();
  });

  it('offers Retry and a way back for any other failed read, and retries the same read', async () => {
    jest.spyOn(console, 'error').mockImplementation(() => undefined);
    const getById = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })))
      .mockReturnValueOnce(of(detail()));
    const { fixture, host } = await render({ id: '7', getById });

    expect(testId(host, 'expense-form-load-error')?.textContent).toContain('This expense could not be loaded.');
    expect(testId(host, 'expense-form-load-error-back')).not.toBeNull();

    (testId(host, 'expense-form-retry') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(getById).toHaveBeenCalledTimes(2);
    expect(getById).toHaveBeenLastCalledWith(7);
    expect(testId(host, 'expense-form-load-error')).toBeNull();
    expect(testId(host, 'expense-form')).not.toBeNull();
  });

  it('loads the expense the address names again when the address changes under it', async () => {
    const { paramMap, getById } = await render({ id: '7' });

    paramMap.next(convertToParamMap({ id: '9' }));

    expect(getById).toHaveBeenCalledTimes(2);
    expect(getById).toHaveBeenLastCalledWith(9);
  });
});

/**
 * Saving an edit (issue #1167). The update is the same request the register's card made, with the
 * same payload - including the three fields this form has no control for.
 */
describe('ExpenseFormPageComponent saving an edit (issue #1167)', () => {
  it('sends siteId, servicePeriodStart and servicePeriodEnd exactly as loaded', async () => {
    const { fixture, update } = await render({ id: '7' });

    saveWith(fixture, 'Annual policy (renewed)');

    expect(update).toHaveBeenCalledTimes(1);
    expect(update.mock.calls[0][0]).toBe(7);
    expect(update.mock.calls[0][1]).toMatchObject({
      description: 'Annual policy (renewed)',
      siteId: 12,
      servicePeriodStart: '2026-02-01T00:00:00Z',
      servicePeriodEnd: '2027-01-31T00:00:00Z'
    });
  });

  it('sends no attachment when none was chosen, so the stored document is untouched', async () => {
    const { fixture, update } = await render({ id: '7', });

    saveWith(fixture);

    expect(update.mock.calls[0][2]).toBeNull();
  });

  it('sends one request for a double press', async () => {
    const { fixture, update } = await render({ id: '7', update: jest.fn(() => NEVER) });

    saveWith(fixture);
    form(fixture).submit();
    fixture.componentInstance.onSave({ payload: form(fixture).form, attachment: null });

    expect(update).toHaveBeenCalledTimes(1);
  });

  it('shows the stored document and fetches it through the authorized API', async () => {
    const { fixture, host, getAttachment } = await render({
      id: '7',
      getById: jest.fn(() => of(detail({ attachmentFileName: 'policy.pdf' })))
    });
    jest.spyOn(window, 'open').mockImplementation(() => null);

    expect(testId(host, 'expense-current-attachment')?.textContent).toContain('policy.pdf');
    (testId(host, 'expense-view-document') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(getAttachment).toHaveBeenCalledWith(7);
  });
});

/**
 * A refused save keeps the operator's work (issue #1167): the page stays open with every value and
 * the chosen document, says what the server said, and takes focus so the refusal is announced.
 */
describe('ExpenseFormPageComponent refused saves (issue #1167)', () => {
  it("shows the server's own sentence for a 400 and keeps every entered value", async () => {
    const update = jest.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 400, error: 'Amounts cannot be negative.' }))
    );
    const { fixture, host, router } = await render({ id: '7', update });
    const navigate = jest.spyOn(router, 'navigate').mockResolvedValue(true);

    form(fixture).form.amountExGst = -5;
    saveWith(fixture, 'Annual policy (renewed)');

    expect(testId(host, 'expense-form-error')?.textContent).toContain('Amounts cannot be negative.');
    expect(navigate).not.toHaveBeenCalled();
    expect(form(fixture).form.description).toBe('Annual policy (renewed)');
    expect(form(fixture).form.amountExGst).toBe(-5);
    expect(testId(host, 'expense-form-submit')).not.toBeNull();
  });

  it('moves focus to the refusal, so it is announced and is where the keyboard already is', async () => {
    const update = jest.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 400, error: 'Description is required.' }))
    );
    const { fixture, host } = await render({ id: '7', update });

    saveWith(fixture);

    expect(document.activeElement).toBe(testId(host, 'expense-form-error'));
  });

  it('offers to reload the expense after a 409, and reloads it', async () => {
    const getById = jest.fn(() => of(detail()));
    const update = jest.fn(() =>
      throwError(() =>
        new HttpErrorResponse({ status: 409, error: 'This expense\'s supporting document was replaced by someone else.' })
      )
    );
    const { fixture, host } = await render({ id: '7', getById, update });

    saveWith(fixture, 'Annual policy (renewed)');

    expect(fixture.componentInstance.conflict).toBe(true);
    expect(testId(host, 'expense-form-error')?.textContent).toContain('was replaced by someone else');
    expect(testId(host, 'expense-form-error')?.textContent).toContain('Somebody else changed this expense');

    (testId(host, 'expense-form-reload') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(getById).toHaveBeenCalledTimes(2);
    expect(form(fixture).form.description).toBe('Annual policy');
    expect(testId(host, 'expense-form-error')).toBeNull();
  });

  it('offers no reload for a refusal that is not a conflict', async () => {
    const update = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: 'Unknown machine.' })));
    const { fixture, host } = await render({ id: '7', update });

    saveWith(fixture);

    expect(testId(host, 'expense-form-reload')).toBeNull();
  });

  it('says the expense was not saved when the request never reached the API', async () => {
    const update = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 0, error: new ProgressEvent('error') })));
    const { fixture, host } = await render({ id: '7', update });

    saveWith(fixture);

    expect(testId(host, 'expense-form-error')?.textContent).toContain('The expense was not saved.');
  });

  it('lets a second press retry exactly what was entered', async () => {
    const update = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 400, error: 'Unknown machine.' })))
      .mockReturnValueOnce(of(detail()));
    const { fixture, router } = await render({ id: '7', update });
    const navigate = jest.spyOn(router, 'navigate').mockResolvedValue(true);

    saveWith(fixture, 'Annual policy (renewed)');
    form(fixture).submit();
    fixture.detectChanges();

    expect(update).toHaveBeenCalledTimes(2);
    expect(update.mock.calls[1][1]).toMatchObject({ description: 'Annual policy (renewed)' });
    expect(navigate).toHaveBeenCalledWith(['/expenses'], { queryParams: {} });
  });
});

/**
 * The return context (issue #1167): the register's applied filters travel as query parameters, and
 * every way off these pages carries them back.
 */
describe('ExpenseFormPageComponent return context (issue #1167)', () => {
  const RETURN = { from: '2026-01-01', to: '2026-01-31', category: '1', supplierId: '3', machineId: '42' };

  it('returns to the register with the filters it was opened from', async () => {
    const { fixture, router } = await render({ id: '7', queryParams: RETURN });
    const navigate = jest.spyOn(router, 'navigate').mockResolvedValue(true);

    saveWith(fixture);

    expect(navigate).toHaveBeenCalledWith(['/expenses'], { queryParams: RETURN });
  });

  it('carries them on Cancel and on Back to expenses', async () => {
    const { host } = await render({ id: '7', queryParams: RETURN });

    const expected = '/expenses?from=2026-01-01&to=2026-01-31&category=1&supplierId=3&machineId=42';
    expect(testId(host, 'expense-form-cancel')?.getAttribute('href')).toBe(expected);
    expect(testId(host, 'expense-form-back')?.getAttribute('href')).toBe(expected);
  });

  it('returns to the plain register when it was opened without them', async () => {
    const { fixture, host, router } = await render({ id: '7' });
    const navigate = jest.spyOn(router, 'navigate').mockResolvedValue(true);

    expect(testId(host, 'expense-form-cancel')?.getAttribute('href')).toBe('/expenses');
    saveWith(fixture);

    expect(navigate).toHaveBeenCalledWith(['/expenses'], { queryParams: {} });
  });

  it('ignores a parameter value this application cannot use rather than refusing the page', async () => {
    const { fixture, host, router } = await render({
      id: '7',
      queryParams: { from: 'last-tuesday', category: '99', supplierId: 'abc', sortBy: 'total', machineId: '42' }
    });
    const navigate = jest.spyOn(router, 'navigate').mockResolvedValue(true);

    expect(testId(host, 'expense-form-cancel')?.getAttribute('href')).toBe('/expenses?machineId=42');
    saveWith(fixture);

    expect(navigate).toHaveBeenCalledWith(['/expenses'], { queryParams: { machineId: '42' } });
  });
});

/**
 * The leave guard (issue #1167), following the New/Edit Product pattern: an in-app departure from
 * unsaved work asks first, and a successful save leaves without asking.
 */
describe('ExpenseFormPageComponent leaving the page (issue #1167)', () => {
  it('asks nothing while nothing has been edited', async () => {
    const { component } = await render({ id: '7' });
    const confirm = jest.spyOn(window, 'confirm');

    expect(component.leaveWarning()).toBeNull();
    expect(component.confirmLeave()).toBe(true);
    expect(confirm).not.toHaveBeenCalled();
  });

  it('asks before discarding an edit, and stays when the answer is no', async () => {
    const { fixture, component } = await render({ id: '7' });
    const confirm = jest.spyOn(window, 'confirm').mockReturnValue(false);

    form(fixture).form.description = 'Edited';

    expect(component.leaveWarning()).toBe(EXPENSE_UNSAVED_LEAVE_WARNING);
    expect(component.confirmLeave()).toBe(false);
    expect(confirm).toHaveBeenCalledWith(EXPENSE_UNSAVED_LEAVE_WARNING);
  });

  it('leaves once the operator confirms', async () => {
    const { fixture, component } = await render({ id: '7' });
    jest.spyOn(window, 'confirm').mockReturnValue(true);

    form(fixture).form.description = 'Edited';

    expect(component.confirmLeave()).toBe(true);
  });

  it('asks nothing after a successful save', async () => {
    const { fixture, component, router } = await render({ id: '7' });
    jest.spyOn(router, 'navigate').mockResolvedValue(true);
    const confirm = jest.spyOn(window, 'confirm');

    saveWith(fixture, 'Annual policy (renewed)');

    expect(component.leaveWarning()).toBeNull();
    expect(component.confirmLeave()).toBe(true);
    expect(confirm).not.toHaveBeenCalled();
  });

  it('still asks after a refused save, because the work is still unsaved', async () => {
    const update = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 400, error: 'Unknown machine.' })));
    const { fixture, component } = await render({ id: '7', update });

    saveWith(fixture, 'Annual policy (renewed)');

    expect(component.leaveWarning()).toBe(EXPENSE_UNSAVED_LEAVE_WARNING);
  });

  it('raises the browser prompt for a reload or a closed tab only while there is work to lose', async () => {
    const { fixture, component } = await render({ id: '7' });
    const clean = new Event('beforeunload') as BeforeUnloadEvent;
    const preventClean = jest.spyOn(clean, 'preventDefault');

    component.onBeforeUnload(clean);
    expect(preventClean).not.toHaveBeenCalled();

    form(fixture).form.description = 'Edited';
    const dirty = new Event('beforeunload') as BeforeUnloadEvent;
    const preventDirty = jest.spyOn(dirty, 'preventDefault');

    component.onBeforeUnload(dirty);
    expect(preventDirty).toHaveBeenCalled();
  });
});

/**
 * A support session (issue #659, extending #554) grants `Expenses.Manage`, the capability these
 * pages are gated on, so each page refuses the write itself rather than offering a form that cannot
 * save. Reading an expense is not a write, so Edit still shows it.
 */
describe('ExpenseFormPageComponent during a support session (issue #1167)', () => {
  it('is the sentence the issue specifies', () => {
    expect(EXPENSE_SUPPORT_SESSION_NOTE).toBe("Expenses can't be added during a support session.");
  });

  it('withholds the Add expense form entirely, and says why', async () => {
    const { host } = await render({ id: null, sessionActive: true });

    expect(testId(host, 'expense-form-support-session')?.textContent).toContain(EXPENSE_SUPPORT_SESSION_NOTE);
    expect(testId(host, 'expense-form')).toBeNull();
    expect(testId(host, 'expense-form-session-back')?.textContent?.trim()).toBe('Back to expenses');
  });

  it('shows an existing expense read-only, with no primary action, and says why', async () => {
    const { fixture, host } = await render({ id: '7', sessionActive: true });

    expect(testId(host, 'expense-form-support-session')?.textContent).toContain(EXPENSE_SUPPORT_SESSION_NOTE);
    expect(testId(host, 'expense-form')).not.toBeNull();
    expect(testId(host, 'expense-form-submit')).toBeNull();
    expect((testId(host, 'expense-form-fields') as HTMLFieldSetElement).disabled).toBe(true);
    expect(form(fixture).form.description).toBe('Annual policy');
  });

  it('shows one and the same notice on Add expense and on Edit expense', async () => {
    const { fixture, host, paramMap } = await render({ id: null, sessionActive: true });
    const note = () => testId(host, 'expense-form-support-session')?.textContent?.trim();

    expect(heading(host)).toBe('Add expense');
    expect(note()).toBe(EXPENSE_SUPPORT_SESSION_NOTE);

    // The same page carried to an edit address: the notice is one shared sentence, so it does not
    // change with the address it is shown on.
    paramMap.next(convertToParamMap({ id: '7' }));
    fixture.detectChanges();

    expect(heading(host)).toBe('Edit expense');
    expect(note()).toBe(EXPENSE_SUPPORT_SESSION_NOTE);
  });

  it('writes nothing even if the page\'s save handler is invoked directly', async () => {
    const { fixture, component, create, update } = await render({ id: '7', sessionActive: true });

    component.onSave({ payload: form(fixture).form, attachment: null });

    expect(update).not.toHaveBeenCalled();
    expect(create).not.toHaveBeenCalled();
  });
});
