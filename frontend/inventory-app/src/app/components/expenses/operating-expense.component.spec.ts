import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, Observable, of } from 'rxjs';
import { OperatingExpenseComponent } from './operating-expense.component';
import { OperatingExpense, OperatingExpenseCategory, OperatingExpenseService } from '../../services/operating-expense.service';
import { SupplierService } from '../../services/supplier.service';
import { LocalMachineService } from '../../services/local-machine.service';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';
import { SupportSessionService } from '../../support-session/support-session.service';

function expense(overrides: Partial<OperatingExpense> = {}): OperatingExpense {
  return {
    id: 1,
    expenseDate: '2026-01-15T00:00:00Z',
    category: OperatingExpenseCategory.Insurance,
    description: 'Annual policy',
    amountExGst: 100,
    gstAmount: 10,
    totalAmount: 110,
    ...overrides
  };
}

async function render(overrides: {
  sessionActive?: Observable<boolean>;
  remove?: jest.Mock;
  expenses?: OperatingExpense[];
  queryParams?: Record<string, string>;
} = {}) {
  const remove = overrides.remove ?? jest.fn(() => of(undefined));
  const getAll = jest.fn(() => of(overrides.expenses ?? [expense()]));
  const queryParamMap = new BehaviorSubject(convertToParamMap(overrides.queryParams ?? {}));

  await TestBed.configureTestingModule({
    imports: [OperatingExpenseComponent],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { queryParamMap } },
      {
        provide: OperatingExpenseService,
        useValue: {
          getAll,
          create: jest.fn(() => of(expense())),
          update: jest.fn(() => of(expense())),
          delete: remove
        }
      },
      { provide: SupplierService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: LocalMachineService, useValue: { getAll: jest.fn(() => of([])) } },
      { provide: SupportSessionService, useValue: { active$: overrides.sessionActive ?? of(false) } },
      provideNayaxConnectionStatus()
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(OperatingExpenseComponent);
  fixture.detectChanges();
  return {
    fixture,
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    getAll,
    remove,
    queryParamMap,
    router: TestBed.inject(Router)
  };
}

const linkHrefs = (host: HTMLElement, testId: string) =>
  Array.from(host.querySelectorAll(`a[data-testid="${testId}"]`)).map((link) => link.getAttribute('href'));

const applyFilters = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === 'Apply filters')!;

afterEach(() => TestBed.resetTestingModule());

/**
 * Hiding expense write controls and guarding their handlers during a support session (issue #659,
 * extending #554). A session grants `ExpensesManage`, so this page's controls would otherwise
 * still render and its handlers would otherwise still call the API.
 *
 * Since issue #1167 Add expense and Edit are links to the dedicated pages rather than buttons that
 * open a card here, so what the register must withhold is those links; the pages refuse the write
 * themselves as well (see `expense-form-page.component.spec.ts`). Since issue #1170 Delete is a
 * menu item in each row's overflow menu, behind a confirmation, so what the register withholds is
 * that whole menu - and both halves of the delete, the ask and the write, are guarded.
 */
describe('OperatingExpenseComponent write controls during a support session (issue #659)', () => {
  it('hides Add expense, Edit and the delete menu while a session is active', async () => {
    const { host } = await render({ sessionActive: of(true) });

    expect(linkHrefs(host, 'expenses-add')).toEqual([]);
    expect(linkHrefs(host, 'expenses-edit')).toEqual([]);
    expect(host.querySelector('[data-testid="expense-row-menu"]')).toBeNull();
    expect(host.querySelector('[data-testid="expense-card-menu"]')).toBeNull();
  });

  it('shows Add expense, Edit and the delete menu while no session is active', async () => {
    const { fixture, host } = await render({ sessionActive: of(false) });

    expect(linkHrefs(host, 'expenses-add')).toEqual(['/expenses/new']);
    expect(linkHrefs(host, 'expenses-edit')).toEqual(['/expenses/1/edit']);

    (host.querySelector('[data-testid="expense-row-menu"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="expense-delete"]')?.textContent?.trim()).toBe('Delete');
  });

  it('does not even ask while a session is active, if the handler is invoked directly', async () => {
    const { component, fixture, host, remove } = await render({ sessionActive: of(true) });

    component.confirmDelete(expense());
    fixture.detectChanges();

    expect(component.confirmingExpense).toBeNull();
    expect(host.querySelector('.confirm-modal')).toBeNull();
    expect(remove).not.toHaveBeenCalled();
  });

  it('does not delete while a session is active, even if the confirmation is invoked directly', async () => {
    const { component, remove } = await render({ sessionActive: of(true) });

    component.deleteConfirmed(expense());

    expect(remove).not.toHaveBeenCalled();
  });

  it('deletes normally, once confirmed, while no session is active', async () => {
    const { component, remove } = await render({ sessionActive: of(false) });

    component.confirmDelete(expense());
    expect(remove).not.toHaveBeenCalled();

    component.deleteConfirmed(expense());

    expect(remove).toHaveBeenCalledWith(1);
  });
});

/**
 * The register records nothing itself since issue #1167: Add Expense and each row's Edit are links
 * to `/expenses/new` and `/expenses/:id/edit`, and no form is left on this page.
 */
describe('OperatingExpenseComponent Add and Edit navigation (issue #1167)', () => {
  it('links Add Expense to the Add expense page', async () => {
    const { host } = await render();

    expect(linkHrefs(host, 'expenses-add')).toEqual(['/expenses/new']);
  });

  it("links each row's Edit to that expense's Edit page", async () => {
    const { host } = await render({ expenses: [expense({ id: 7 }), expense({ id: 9 })] });

    expect(linkHrefs(host, 'expenses-edit')).toEqual(['/expenses/7/edit', '/expenses/9/edit']);
  });

  it('leaves no expense form on the register', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="expense-form"]')).toBeNull();
    expect(host.querySelector('#expense-description')).toBeNull();
    expect(host.querySelector('input[type="file"]')).toBeNull();
  });
});

/**
 * The applied filters are the register's URL query parameters (issue #1167), which is what lets Add
 * and Edit bring the operator back to the register they left.
 */
describe('OperatingExpenseComponent filters as query parameters (issue #1167)', () => {
  it('applies the filters the address asks for, and reads the list with them', async () => {
    const { component, getAll } = await render({
      queryParams: { from: '2026-01-01', to: '2026-01-31', category: '1', supplierId: '3', machineId: '42' }
    });

    expect(component.filters).toEqual({
      from: '2026-01-01',
      to: '2026-01-31',
      category: OperatingExpenseCategory.Insurance,
      supplierId: 3,
      machineId: 42
    });
    expect(getAll).toHaveBeenLastCalledWith({
      from: '2026-01-01',
      to: '2026-01-31',
      category: OperatingExpenseCategory.Insurance,
      supplierId: 3,
      machineId: 42
    });
  });

  it('keeps the Nayax monthly fee as a filterable category, which is parameter value 0', async () => {
    const { component, getAll } = await render({ queryParams: { category: '0' } });

    expect(component.filters.category).toBe(OperatingExpenseCategory.NayaxMonthlyFee);
    expect(getAll).toHaveBeenLastCalledWith({ category: OperatingExpenseCategory.NayaxMonthlyFee });
  });

  it('ignores unknown and unusable parameter values rather than refusing the page', async () => {
    const { component, getAll } = await render({
      queryParams: {
        from: 'last-tuesday',
        to: '',
        category: '99',
        supplierId: 'abc',
        machineId: '0',
        sortBy: 'total'
      }
    });

    expect(component.filters).toEqual({});
    expect(getAll).toHaveBeenLastCalledWith({});
  });

  it('writes the chosen filters to the address when Apply filters is pressed', async () => {
    const { component, fixture, host, router } = await render();
    const navigate = jest.spyOn(router, 'navigate').mockResolvedValue(true);

    component.filters = { from: '2026-02-01', category: OperatingExpenseCategory.Software };
    fixture.detectChanges();
    applyFilters(host).click();

    expect(navigate).toHaveBeenCalledWith(['/expenses'], { queryParams: { from: '2026-02-01', category: '3' } });
  });

  it('re-reads the list when Apply filters names the register already shown', async () => {
    const { host, getAll, router } = await render({ queryParams: { from: '2026-02-01' } });
    const navigate = jest.spyOn(router, 'navigate').mockResolvedValue(true);
    expect(getAll).toHaveBeenCalledTimes(1);

    applyFilters(host).click();

    expect(navigate).not.toHaveBeenCalled();
    expect(getAll).toHaveBeenCalledTimes(2);
    expect(getAll).toHaveBeenLastCalledWith({ from: '2026-02-01' });
  });

  it('carries the applied filters, not the half-chosen ones, on the Add and Edit links', async () => {
    const { component, fixture, host } = await render({ queryParams: { supplierId: '3' } });

    // Changing a control without pressing Apply changes what the register is looking at not at all.
    component.filters = { supplierId: 8 };
    fixture.detectChanges();

    expect(linkHrefs(host, 'expenses-add')).toEqual(['/expenses/new?supplierId=3']);
    expect(linkHrefs(host, 'expenses-edit')).toEqual(['/expenses/1/edit?supplierId=3']);
  });

  it('re-reads the list when the address changes under it', async () => {
    const { getAll, queryParamMap } = await render({ queryParams: { from: '2026-02-01' } });

    queryParamMap.next(convertToParamMap({ from: '2026-03-01' }));

    expect(getAll).toHaveBeenCalledTimes(2);
    expect(getAll).toHaveBeenLastCalledWith({ from: '2026-03-01' });
  });
});
