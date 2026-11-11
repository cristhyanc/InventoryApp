import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Params, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, NEVER, Observable, of, throwError } from 'rxjs';
import { LocalMachine } from '../../models/models';
import { provideNayaxConnectionStatus } from '../../provider/provider-availability.spec-helpers';
import { LocalMachineService } from '../../services/local-machine.service';
import { OperatingExpense, OperatingExpenseCategory, OperatingExpenseService } from '../../services/operating-expense.service';
import { SupplierService } from '../../services/supplier.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { OperatingExpenseComponent } from './operating-expense.component';

function expense(id: number, overrides: Partial<OperatingExpense> = {}): OperatingExpense {
  return {
    id,
    expenseDate: '2026-01-15T00:00:00',
    category: OperatingExpenseCategory.Insurance,
    description: `Expense ${String(id).padStart(2, '0')}`,
    amountExGst: 100,
    gstAmount: 10,
    totalAmount: 110,
    ...overrides
  };
}

const many = (count: number) => Array.from({ length: count }, (_, index) => expense(index + 1));

async function render(
  options: {
    expenses?: OperatingExpense[];
    list?: Observable<OperatingExpense[]>;
    getAll?: jest.Mock;
    remove?: jest.Mock;
    attachment?: jest.Mock;
    machines?: Observable<LocalMachine[]>;
    sessionActive?: boolean;
    queryParams?: Record<string, string>;
  } = {}
) {
  const getAll = options.getAll ?? jest.fn(() => options.list ?? of(options.expenses ?? [expense(1)]));
  const remove = options.remove ?? jest.fn(() => of(undefined));
  const getAttachment = options.attachment ?? jest.fn(() => of(new Blob(['pdf'])));
  const queryParamMap = new BehaviorSubject(convertToParamMap(options.queryParams ?? {}));

  await TestBed.configureTestingModule({
    imports: [OperatingExpenseComponent],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { queryParamMap } },
      {
        provide: OperatingExpenseService,
        useValue: { getAll, delete: remove, getAttachment }
      },
      { provide: SupplierService, useValue: { getAll: jest.fn(() => of([{ id: 3, name: 'Broker' }])) } },
      {
        provide: LocalMachineService,
        useValue: {
          getAll: jest.fn(() => options.machines ?? of([{ id: 41, name: 'Lobby', isActive: true, isProviderNamed: false }]))
        }
      },
      { provide: SupportSessionService, useValue: { active$: of(options.sessionActive ?? false) } },
      provideNayaxConnectionStatus('Ready')
    ]
  }).compileComponents();

  // The router is spied on before the first change detection, because reading the list is also what
  // settles a page the address asks for but the filtered list does not have.
  const navigate = jest.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture: ComponentFixture<OperatingExpenseComponent> = TestBed.createComponent(OperatingExpenseComponent);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;

  /** Follows a navigation the way the real router would: the address is what the register reads. */
  const arrive = () => {
    const call = navigate.mock.calls.at(-1);
    queryParamMap.next(convertToParamMap(((call?.[1]?.queryParams ?? {}) as Params) as Record<string, string>));
    fixture.detectChanges();
  };

  return { fixture, host, component: fixture.componentInstance, getAll, remove, getAttachment, navigate, queryParamMap, arrive };
}

const testId = (host: HTMLElement, id: string) => host.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const click = (host: HTMLElement, id: string) => (testId(host, id) as HTMLButtonElement).click();
const rowDescriptions = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('[data-testid="expense-row"] td:nth-child(2)')).map((cell) => cell.textContent?.trim());
const cardDescriptions = (host: HTMLElement) =>
  Array.from(host.querySelectorAll('[data-testid="expense-card-description"]')).map((cell) => cell.textContent?.trim());
const dialogMessage = (host: HTMLElement) => host.querySelector('.confirm-modal p')?.textContent?.trim();
const dialogButton = (host: HTMLElement, label: string) =>
  Array.from(host.querySelectorAll('.confirm-modal button')).find((button) => button.textContent?.trim() === label) as
    | HTMLButtonElement
    | undefined;

afterEach(() => TestBed.resetTestingModule());

/**
 * The Operating Expenses register redesign (issue #1170): the compact toolbar with its More filters
 * disclosure, the nine-column table and the narrow-screen cards over one page of the filtered list,
 * the pager in the address, the Totals footer over **all** filtered rows, and the delete flow behind
 * an overflow menu and a confirmation. Every assertion is on what the operator sees or what the page
 * requests, never on markup for its own sake.
 */
describe('OperatingExpenseComponent redesign (issue #1170)', () => {
  describe('header and toolbar', () => {
    it('heads the register, keeps Add expense primary, and offers the date range and Category directly', async () => {
      const { host } = await render();

      expect(host.querySelector('h1')?.textContent?.trim()).toBe('Operating expenses');
      expect(testId(host, 'expenses-add')?.textContent?.trim()).toContain('Add expense');
      expect(testId(host, 'expenses-toolbar')?.querySelector('#expense-from-filter')).not.toBeNull();
      expect(testId(host, 'expenses-toolbar')?.querySelector('#expense-to-filter')).not.toBeNull();
      expect(testId(host, 'expenses-toolbar')?.querySelector('#expense-category-filter')).not.toBeNull();
    });

    it('keeps Supplier and Machine behind More filters, closed and announced as such until it is opened', async () => {
      const { fixture, host } = await render();
      const panel = () => testId(host, 'expenses-more-filters-panel')!;

      expect(testId(host, 'expenses-more-filters')?.getAttribute('aria-expanded')).toBe('false');
      expect(panel().hidden).toBe(true);
      expect(panel().querySelector('#expense-supplier-filter')).not.toBeNull();
      expect(panel().querySelector('#expense-machine-filter')).not.toBeNull();

      click(host, 'expenses-more-filters');
      fixture.detectChanges();

      expect(testId(host, 'expenses-more-filters')?.getAttribute('aria-expanded')).toBe('true');
      expect(panel().hidden).toBe(false);
    });

    it('opens More filters by itself when the address already applies one of the filters it holds', async () => {
      const { host } = await render({ queryParams: { machineId: '41' } });

      expect(testId(host, 'expenses-more-filters-panel')?.hidden).toBe(false);
    });

    it('applies filters only when Apply filters is pressed, and writes them to the address', async () => {
      const { component, fixture, host, getAll, navigate } = await render();

      component.filters = { from: '2026-02-01', supplierId: 3 };
      fixture.detectChanges();
      expect(getAll).toHaveBeenCalledTimes(1);
      expect(getAll).toHaveBeenLastCalledWith({});

      click(host, 'expenses-apply-filters');

      expect(navigate).toHaveBeenCalledWith(['/expenses'], { queryParams: { from: '2026-02-01', supplierId: '3' } });
    });

    it('reports nothing and offers no Clear filters on the unfiltered register', async () => {
      const { host } = await render();

      expect(testId(host, 'expenses-filter-count')).toBeNull();
      expect(testId(host, 'expenses-clear-filters')).toBeNull();
    });

    it('reports how many filters are applied, and offers Clear filters', async () => {
      const { host } = await render({ queryParams: { from: '2026-01-01', category: '1', machineId: '41' } });

      expect(testId(host, 'expenses-filter-count')?.textContent?.trim()).toBe('3 filters applied');
      expect(testId(host, 'expenses-clear-filters')).not.toBeNull();
    });

    it('offers Clear filters as soon as a filter is chosen, before it is applied', async () => {
      const { component, fixture, host } = await render();

      component.filters = { category: OperatingExpenseCategory.Software };
      fixture.detectChanges();

      expect(testId(host, 'expenses-clear-filters')).not.toBeNull();
      expect(testId(host, 'expenses-filter-count')).toBeNull();
    });

    it('clears every filter back to the default register, on page 1, and re-reads it', async () => {
      const { host, navigate, arrive, getAll, component } = await render({
        queryParams: { from: '2026-01-01', category: '1', page: '2', pageSize: '5' }
      });

      click(host, 'expenses-clear-filters');
      arrive();

      expect(navigate).toHaveBeenCalledWith(['/expenses'], { queryParams: { pageSize: '5' } });
      expect(component.appliedFilters).toEqual({});
      expect(component.pageIndex).toBe(0);
      expect(getAll).toHaveBeenLastCalledWith({});
      expect(getAll).toHaveBeenCalledTimes(2);
    });
  });

  describe('desktop table', () => {
    it('shows the nine columns in order, with the date and currency formatting the register has always used', async () => {
      const { host } = await render({
        expenses: [expense(1, { description: 'Annual policy', supplierName: 'Broker', amountExGst: 0, gstAmount: 0, totalAmount: 0 })]
      });

      expect(Array.from(host.querySelectorAll('thead th')).map((th) => th.textContent?.trim())).toEqual([
        'Date',
        'Description',
        'Category',
        'Supplier',
        'Document',
        'Ex GST',
        'GST',
        'Total',
        'Actions'
      ]);
      const cells = Array.from(host.querySelectorAll('[data-testid="expense-row"] td')).map((td) => td.textContent?.trim());
      expect(cells.slice(0, 4)).toEqual(['2026-01-15', 'Annual policy', 'Insurance', 'Broker']);
      // A zero amount is $0.00, never a dash: it is a recorded figure.
      expect(cells.slice(5, 8)).toEqual(['$0.00', '$0.00', '$0.00']);
    });

    it('shows an em dash for a missing supplier and for a missing document', async () => {
      const { host } = await render({ expenses: [expense(1)] });

      const cells = Array.from(host.querySelectorAll('[data-testid="expense-row"] td')).map((td) => td.textContent?.trim());
      expect(cells[3]).toBe('—');
      expect(cells[4]).toBe('—');
      expect(testId(host, 'expense-view-document')).toBeNull();
    });

    it('totals Ex GST, GST and Total over every filtered row, not only the page on screen', async () => {
      const { host } = await render({ expenses: many(23) });

      expect(rowDescriptions(host)).toHaveLength(10);
      expect(
        Array.from(testId(host, 'expenses-totals')!.querySelectorAll('td')).map((cell) => cell.textContent?.trim())
      ).toEqual(['Totals (all 23 filtered expenses)', '$2,300.00', '$230.00', '$2,530.00', '']);
    });

    it('opens the document through the service, and says so on the page when that read fails', async () => {
      const attachment = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
      const { fixture, host, getAttachment } = await render({
        attachment,
        expenses: [expense(1, { description: 'Annual policy', attachmentFileName: 'policy.pdf' })]
      });

      click(host, 'expense-view-document');
      fixture.detectChanges();

      expect(getAttachment).toHaveBeenCalledWith(1);
      expect(testId(host, 'expenses-action-error')?.textContent).toContain('could not be opened');
      expect(testId(host, 'expenses-action-error')?.textContent).toContain('Annual policy');
    });

    it('carries the applied filters and the page on every row Edit link', async () => {
      const { host } = await render({ expenses: many(23), queryParams: { supplierId: '3', page: '2' } });

      expect(testId(host, 'expenses-edit')?.getAttribute('href')).toBe('/expenses/11/edit?supplierId=3&page=2');
    });
  });

  describe('paging', () => {
    it('shows ten expenses per page by default and offers exactly 5, 10, 20, 50 and 100', async () => {
      const { host } = await render({ expenses: many(23) });

      expect(rowDescriptions(host)).toHaveLength(10);
      const select = testId(host, 'list-pagination-size') as HTMLSelectElement;
      expect(Array.from(select.options).map((option) => Number(option.value))).toEqual([5, 10, 20, 50, 100]);
      expect(testId(host, 'list-pagination-range')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
        'Showing 1–10 of 23 expenses'
      );
    });

    it('opens the page and size the address asks for, without requesting them from the API', async () => {
      const { host, getAll } = await render({ expenses: many(23), queryParams: { pageSize: '5', page: '3' } });

      expect(rowDescriptions(host)).toEqual(['Expense 11', 'Expense 12', 'Expense 13', 'Expense 14', 'Expense 15']);
      expect(getAll).toHaveBeenCalledTimes(1);
      expect(getAll).toHaveBeenLastCalledWith({});
    });

    it('writes a turned page to the address, keeping the filters, and reads nothing again', async () => {
      const { host, navigate, arrive, getAll } = await render({ expenses: many(23), queryParams: { from: '2026-01-01' } });

      click(host, 'list-pagination-next');
      arrive();

      expect(navigate).toHaveBeenLastCalledWith(['/expenses'], {
        queryParams: { from: '2026-01-01', page: '2' },
        replaceUrl: false
      });
      expect(rowDescriptions(host)).toEqual([
        'Expense 11',
        'Expense 12',
        'Expense 13',
        'Expense 14',
        'Expense 15',
        'Expense 16',
        'Expense 17',
        'Expense 18',
        'Expense 19',
        'Expense 20'
      ]);
      expect(getAll).toHaveBeenCalledTimes(1);
    });

    it('returns to page 1 when the page size changes', async () => {
      const { fixture, host, component, navigate, arrive } = await render({
        expenses: many(23),
        queryParams: { page: '3' }
      });

      const select = testId(host, 'list-pagination-size') as HTMLSelectElement;
      select.value = '5';
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();
      arrive();

      expect(navigate).toHaveBeenLastCalledWith(['/expenses'], { queryParams: { pageSize: '5' }, replaceUrl: false });
      expect(component.pageIndex).toBe(0);
      expect(rowDescriptions(host)).toEqual(['Expense 01', 'Expense 02', 'Expense 03', 'Expense 04', 'Expense 05']);
    });

    it('returns to page 1 when a filter is applied from a later page', async () => {
      const { component, fixture, host, navigate } = await render({ expenses: many(23), queryParams: { page: '3' } });

      component.filters = { category: OperatingExpenseCategory.Software };
      fixture.detectChanges();
      click(host, 'expenses-apply-filters');

      expect(navigate).toHaveBeenLastCalledWith(['/expenses'], { queryParams: { category: '3' } });
    });

    it('settles a page the filtered list does not have onto the last one that exists', async () => {
      const { component, navigate, arrive } = await render({ expenses: many(12), queryParams: { page: '9' } });

      expect(navigate).toHaveBeenLastCalledWith(['/expenses'], { queryParams: { page: '2' }, replaceUrl: true });
      arrive();
      expect(component.pageIndex).toBe(1);
    });
  });

  describe('narrow-screen cards', () => {
    it('render the same page of expenses, in the same order, as the table', async () => {
      const { host } = await render({ expenses: many(23), queryParams: { page: '2' } });

      expect(cardDescriptions(host)).toEqual(rowDescriptions(host));
      expect(cardDescriptions(host)).toHaveLength(10);
    });

    it('show the whole description, the amounts, the document and the same two actions', async () => {
      const { host } = await render({
        expenses: [
          expense(1, {
            description: 'Annual public liability insurance policy for the whole fleet, renewed in January',
            supplierName: 'Broker',
            attachmentFileName: 'policy.pdf'
          })
        ]
      });
      const card = testId(host, 'expense-card')!;

      expect(card.textContent).toContain('Annual public liability insurance policy for the whole fleet, renewed in January');
      expect(card.textContent).toContain('2026-01-15');
      expect(card.textContent).toContain('Insurance');
      expect(card.textContent).toContain('Broker');
      expect(card.textContent).toContain('$110.00');
      expect(card.textContent).toContain('Ex GST $100.00 · GST $10.00');
      expect(card.querySelector('[data-testid="expense-card-view-document"]')).not.toBeNull();
      expect(card.querySelector('[data-testid="expenses-card-edit"]')).not.toBeNull();
      expect(card.querySelector('[data-testid="expense-card-menu"]')).not.toBeNull();
    });

    it('summarises the same all-filtered-rows totals in one line', async () => {
      const { host } = await render({ expenses: many(23) });

      expect(testId(host, 'expenses-totals-summary')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
        'Totals (all 23 filtered expenses): Ex GST $2,300.00 · GST $230.00 · Total $2,530.00'
      );
    });
  });

  describe('delete', () => {
    it('deletes nothing when the row menu is merely opened', async () => {
      const { fixture, host, remove } = await render();

      (testId(host, 'expense-row-menu') as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(remove).not.toHaveBeenCalled();
      expect(host.querySelector('.confirm-modal')).toBeNull();
      expect(testId(host, 'expense-delete')).not.toBeNull();
    });

    it('asks first, naming the expense, and deletes only once Delete is confirmed', async () => {
      const { fixture, host, remove, getAll } = await render({
        expenses: [expense(7, { description: 'Annual policy', totalAmount: 1200 })]
      });

      (testId(host, 'expense-row-menu') as HTMLButtonElement).click();
      fixture.detectChanges();
      click(host, 'expense-delete');
      fixture.detectChanges();

      expect(dialogMessage(host)).toBe('Delete "Annual policy" recorded on 2026-01-15 for $1,200.00? This cannot be undone.');
      expect(remove).not.toHaveBeenCalled();

      dialogButton(host, 'Delete')!.click();
      fixture.detectChanges();

      expect(remove).toHaveBeenCalledWith(7);
      expect(getAll).toHaveBeenCalledTimes(2);
      expect(host.querySelector('.confirm-modal')).toBeNull();
    });

    it('deletes nothing when the confirmation is cancelled', async () => {
      const { fixture, host, remove } = await render();

      (testId(host, 'expense-row-menu') as HTMLButtonElement).click();
      fixture.detectChanges();
      click(host, 'expense-delete');
      fixture.detectChanges();
      dialogButton(host, 'Cancel')!.click();
      fixture.detectChanges();

      expect(remove).not.toHaveBeenCalled();
      expect(host.querySelector('.confirm-modal')).toBeNull();
    });

    it('keeps the row and shows the refusal when the delete fails, and never a success', async () => {
      const remove = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 409, error: 'This expense is locked.' })));
      const { fixture, host, component, getAll } = await render({ remove, expenses: [expense(7, { description: 'Annual policy' })] });

      component.confirmDelete(expense(7, { description: 'Annual policy' }));
      fixture.detectChanges();
      dialogButton(host, 'Delete')!.click();
      fixture.detectChanges();

      expect(testId(host, 'expenses-action-error')?.textContent).toContain('This expense is locked.');
      expect(rowDescriptions(host)).toEqual(['Annual policy']);
      // The list was read once, for the page itself: a failed delete reloads nothing.
      expect(getAll).toHaveBeenCalledTimes(1);
    });

    it('steps back a page when the last row of the last page is deleted, keeping the filters', async () => {
      const getAll = jest.fn().mockReturnValueOnce(of(many(11))).mockReturnValueOnce(of(many(10)));
      const { fixture, host, component, navigate, arrive } = await render({
        getAll,
        queryParams: { from: '2026-01-01', page: '2' }
      });
      expect(rowDescriptions(host)).toEqual(['Expense 11']);

      component.deleteConfirmed(expense(11));
      fixture.detectChanges();

      expect(navigate).toHaveBeenLastCalledWith(['/expenses'], {
        queryParams: { from: '2026-01-01' },
        replaceUrl: true
      });
      arrive();
      expect(component.pageIndex).toBe(0);
      expect(rowDescriptions(host)).toHaveLength(10);
      expect(getAll).toHaveBeenLastCalledWith({ from: '2026-01-01' });
    });

    it('moves focus to a control that still exists once a delete succeeds', async () => {
      const { fixture, host, component } = await render({ expenses: [expense(7)] });
      document.body.appendChild(host);

      component.deleteConfirmed(expense(7));
      fixture.detectChanges();

      expect(document.activeElement).toBe(testId(host, 'expenses-add'));
    });
  });

  describe('states', () => {
    it('says the list is loading rather than showing an empty register', async () => {
      const { host } = await render({ list: NEVER });

      expect(testId(host, 'expenses-loading')).not.toBeNull();
      expect(testId(host, 'expenses-empty')).toBeNull();
      expect(testId(host, 'expenses-table')).toBeNull();
    });

    it('offers Add expense when the business has recorded nothing yet', async () => {
      const { host } = await render({ expenses: [] });

      expect(testId(host, 'expenses-empty')?.textContent).toContain('No operating expenses recorded yet.');
      expect(testId(host, 'expenses-empty-add')).not.toBeNull();
      expect(testId(host, 'expenses-no-match')).toBeNull();
    });

    it('offers Clear filters when the filters match nothing', async () => {
      const { host, navigate } = await render({ expenses: [], queryParams: { from: '2026-01-01' } });

      expect(testId(host, 'expenses-no-match')?.textContent).toContain('No expenses match these filters.');
      click(host, 'expenses-no-match-clear');
      expect(navigate).toHaveBeenCalledWith(['/expenses'], { queryParams: {} });
    });

    it('shows a failed list load, with Retry, instead of showing nothing at all', async () => {
      const getAll = jest
        .fn()
        .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })))
        .mockReturnValueOnce(of([expense(1)]));
      const { fixture, host } = await render({ getAll });

      expect(testId(host, 'expenses-load-error')?.textContent).toContain('could not be loaded');
      expect(testId(host, 'expenses-table')).toBeNull();

      click(host, 'expenses-retry');
      fixture.detectChanges();

      expect(testId(host, 'expenses-load-error')).toBeNull();
      expect(rowDescriptions(host)).toEqual(['Expense 01']);
    });

    it('states nothing the mock claimed that this register cannot know', async () => {
      const { host } = await render({ expenses: many(3) });

      for (const claim of ['Verified', 'BAS', 'Q1', 'quarter', 'ledger', 'reconcil', 'audit']) {
        expect(host.textContent?.toLowerCase()).not.toContain(claim.toLowerCase());
      }
    });
  });

  describe('support session', () => {
    it('withholds Add expense, Edit and every delete control, and still shows the register', async () => {
      const { fixture, host, remove } = await render({ sessionActive: true, expenses: [expense(1, { attachmentFileName: 'policy.pdf' })] });

      expect(testId(host, 'expenses-add')).toBeNull();
      expect(testId(host, 'expenses-edit')).toBeNull();
      expect(testId(host, 'expense-row-menu')).toBeNull();
      expect(testId(host, 'expense-card-menu')).toBeNull();
      // The register itself, and the document it holds, stay readable.
      expect(testId(host, 'expenses-table')).not.toBeNull();
      expect(testId(host, 'expense-view-document')).not.toBeNull();

      fixture.componentInstance.confirmDelete(expense(1));
      fixture.detectChanges();
      expect(host.querySelector('.confirm-modal')).toBeNull();

      fixture.componentInstance.deleteConfirmed(expense(1));
      expect(remove).not.toHaveBeenCalled();
    });
  });
});
