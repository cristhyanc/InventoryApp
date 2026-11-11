import { ParamMap, Params } from '@angular/router';
import { OperatingExpenseCategory } from '../../services/operating-expense.service';

/**
 * The register's applied filters (issue #1167).
 *
 * Since the Add and Edit forms moved onto pages of their own, these are also the register's URL
 * query parameters: Apply filters writes them to the address, opening `/expenses` with them applies
 * them, and Add and Edit carry them so a save, a Cancel or Back to expenses returns to the register
 * the operator left rather than to an unfiltered one.
 */
export interface ExpenseRegisterFilters {
  from?: string;
  to?: string;
  category?: OperatingExpenseCategory;
  supplierId?: number;
  machineId?: number;
}

/** The five parameter names the register publishes, and the only ones it reads back. */
export const EXPENSE_FILTER_PARAM_NAMES = ['from', 'to', 'category', 'supplierId', 'machineId'] as const;

/** The page sizes the register's pager offers (issue #1170), the Products list's own set. */
export const EXPENSE_PAGE_SIZE_OPTIONS = [5, 10, 20, 50, 100] as const;

/** The page size every visit starts at (issue #1170, as on Products). Not persisted. */
export const EXPENSE_DEFAULT_PAGE_SIZE = 10;

/**
 * The whole register address (issue #1170): the filters the list is read with, plus the page of that
 * filtered list on screen. Paging is client-side over the one list already read, so `page` and
 * `pageSize` change what is displayed and never what is requested - but they are in the address for
 * the same reason the filters are, so a link, a bookmark, a reload and the return from Add or Edit
 * all open the register the operator left.
 */
export interface ExpenseRegisterView {
  filters: ExpenseRegisterFilters;
  /** 0-based, as the shared pager counts; `page` is the 1-based page a person reads. */
  pageIndex: number;
  pageSize: number;
}

/** Every parameter name the register publishes: its filters, then its page. */
export const EXPENSE_REGISTER_PARAM_NAMES = [...EXPENSE_FILTER_PARAM_NAMES, 'page', 'pageSize'] as const;

const CALENDAR_DATE = /^\d{4}-\d{2}-\d{2}$/;
const WHOLE_NUMBER = /^\d+$/;

const KNOWN_CATEGORIES = new Set<number>(
  Object.values(OperatingExpenseCategory).filter((value) => typeof value === 'number') as number[]
);

/** A `yyyy-MM-dd` date, which is what the date inputs produce; anything else is ignored. */
function readCalendarDate(value: string | null): string | undefined {
  return value !== null && CALENDAR_DATE.test(value) ? value : undefined;
}

/** A record id: a whole number above zero. A blank, a word or `0` names no row, so it is ignored. */
function readId(value: string | null): number | undefined {
  if (value === null || !WHOLE_NUMBER.test(value)) {
    return undefined;
  }
  const id = Number(value);
  return id > 0 ? id : undefined;
}

/**
 * One of the categories this application knows. An unknown number is ignored rather than filtering
 * the register down to a category nothing can be recorded under - and `0` is a real category
 * (the Nayax monthly fee), so the digits are checked before the value.
 */
function readCategory(value: string | null): OperatingExpenseCategory | undefined {
  if (value === null || !WHOLE_NUMBER.test(value)) {
    return undefined;
  }
  const category = Number(value);
  return KNOWN_CATEGORIES.has(category) ? (category as OperatingExpenseCategory) : undefined;
}

/**
 * The filters a URL asks for. An unknown parameter, and a known one carrying a value this
 * application cannot use, is **ignored**: a stale bookmark or a hand-edited address opens the
 * register rather than an error page.
 */
export function readExpenseFilters(params: ParamMap): ExpenseRegisterFilters {
  const filters: ExpenseRegisterFilters = {};
  const from = readCalendarDate(params.get('from'));
  const to = readCalendarDate(params.get('to'));
  const category = readCategory(params.get('category'));
  const supplierId = readId(params.get('supplierId'));
  const machineId = readId(params.get('machineId'));

  if (from !== undefined) filters.from = from;
  if (to !== undefined) filters.to = to;
  if (category !== undefined) filters.category = category;
  if (supplierId !== undefined) filters.supplierId = supplierId;
  if (machineId !== undefined) filters.machineId = machineId;
  return filters;
}

/**
 * The same filters as query parameters, carrying only the ones that are actually set, so an
 * unfiltered register is the plain `/expenses` address it has always been.
 */
export function expenseFilterQueryParams(filters: ExpenseRegisterFilters): Params {
  const params: Params = {};
  if (filters.from) params['from'] = filters.from;
  if (filters.to) params['to'] = filters.to;
  if (filters.category !== undefined) params['category'] = String(filters.category);
  if (filters.supplierId !== undefined) params['supplierId'] = String(filters.supplierId);
  if (filters.machineId !== undefined) params['machineId'] = String(filters.machineId);
  return params;
}

/** Whether two parameter sets name the same register, so Apply knows a navigation would be a no-op. */
export function sameExpenseFilterParams(left: Params, right: Params): boolean {
  return EXPENSE_FILTER_PARAM_NAMES.every((name) => left[name] === right[name]);
}

/**
 * The page the address asks for, as a 0-based index. `page` is 1-based because that is the page a
 * person reads in "Page 2 of 5"; `0`, a negative number and a word each name no page, so they are
 * ignored and the register opens at the first page.
 */
function readPageIndex(value: string | null): number {
  if (value === null || !WHOLE_NUMBER.test(value)) {
    return 0;
  }
  const page = Number(value);
  return page > 0 ? page - 1 : 0;
}

/** One of the offered page sizes. Anything else (a hand-edited address) falls back to the default. */
function readPageSize(value: string | null): number {
  if (value === null || !WHOLE_NUMBER.test(value)) {
    return EXPENSE_DEFAULT_PAGE_SIZE;
  }
  const size = Number(value);
  return (EXPENSE_PAGE_SIZE_OPTIONS as readonly number[]).includes(size) ? size : EXPENSE_DEFAULT_PAGE_SIZE;
}

/**
 * The filters and the page an address asks for (issue #1170). As with the filters, a parameter
 * carrying a value this application cannot use is **ignored** rather than refused.
 */
export function readExpenseRegisterView(params: ParamMap): ExpenseRegisterView {
  return {
    filters: readExpenseFilters(params),
    pageIndex: readPageIndex(params.get('page')),
    pageSize: readPageSize(params.get('pageSize'))
  };
}

/**
 * The same register as query parameters. The first page at the default size is the plain `/expenses`
 * address it has always been, so only a page or size that differs from the default is published.
 */
export function expenseRegisterQueryParams(view: ExpenseRegisterView): Params {
  const params = expenseFilterQueryParams(view.filters);
  if (view.pageIndex > 0) params['page'] = String(view.pageIndex + 1);
  if (view.pageSize !== EXPENSE_DEFAULT_PAGE_SIZE) params['pageSize'] = String(view.pageSize);
  return params;
}

/** Whether two parameter sets name the same register **and** the same page of it. */
export function sameExpenseRegisterParams(left: Params, right: Params): boolean {
  return EXPENSE_REGISTER_PARAM_NAMES.every((name) => left[name] === right[name]);
}
