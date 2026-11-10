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
