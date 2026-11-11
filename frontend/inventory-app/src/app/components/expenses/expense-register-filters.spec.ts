import { convertToParamMap } from '@angular/router';
import { OperatingExpenseCategory } from '../../services/operating-expense.service';
import {
  EXPENSE_DEFAULT_PAGE_SIZE,
  EXPENSE_PAGE_SIZE_OPTIONS,
  expenseRegisterQueryParams,
  readExpenseRegisterView,
  sameExpenseRegisterParams
} from './expense-register-filters';

const view = (params: Record<string, string>) => readExpenseRegisterView(convertToParamMap(params));

/**
 * The register's page lives in the address beside its filters (issue #1170), so a link, a bookmark,
 * a reload and the return from Add or Edit all open the same page of the same filtered register.
 * Paging itself is client-side over the list already read: these functions only read and write the
 * address.
 */
describe('expense register view parameters (issue #1170)', () => {
  it('defaults to the first page at ten rows, which is the plain /expenses address', () => {
    expect(EXPENSE_DEFAULT_PAGE_SIZE).toBe(10);
    expect([...EXPENSE_PAGE_SIZE_OPTIONS]).toEqual([5, 10, 20, 50, 100]);
    expect(view({})).toEqual({ filters: {}, pageIndex: 0, pageSize: EXPENSE_DEFAULT_PAGE_SIZE });
    expect(expenseRegisterQueryParams({ filters: {}, pageIndex: 0, pageSize: EXPENSE_DEFAULT_PAGE_SIZE })).toEqual({});
  });

  it('reads the 1-based page a person reads as the 0-based index the pager counts', () => {
    expect(view({ page: '3' }).pageIndex).toBe(2);
    expect(expenseRegisterQueryParams({ filters: {}, pageIndex: 2, pageSize: EXPENSE_DEFAULT_PAGE_SIZE })).toEqual({
      page: '3'
    });
  });

  it('reads an offered page size, and publishes it only when it is not the default', () => {
    expect(view({ pageSize: '50' }).pageSize).toBe(50);
    expect(expenseRegisterQueryParams({ filters: {}, pageIndex: 0, pageSize: 50 })).toEqual({ pageSize: '50' });
  });

  it('ignores a page or size it cannot use rather than refusing the register', () => {
    expect(view({ page: '0', pageSize: '7' })).toEqual({
      filters: {},
      pageIndex: 0,
      pageSize: EXPENSE_DEFAULT_PAGE_SIZE
    });
    expect(view({ page: 'last', pageSize: 'all' })).toEqual({
      filters: {},
      pageIndex: 0,
      pageSize: EXPENSE_DEFAULT_PAGE_SIZE
    });
    expect(view({ page: '-2' }).pageIndex).toBe(0);
  });

  it('carries the filters and the page together, so one address names both', () => {
    const read = view({ from: '2026-01-01', category: '1', page: '2', pageSize: '20' });

    expect(read).toEqual({
      filters: { from: '2026-01-01', category: OperatingExpenseCategory.Insurance },
      pageIndex: 1,
      pageSize: 20
    });
    expect(expenseRegisterQueryParams(read)).toEqual({
      from: '2026-01-01',
      category: '1',
      page: '2',
      pageSize: '20'
    });
  });

  it('compares the whole address, so a page change is a change and a reordered one is not', () => {
    expect(sameExpenseRegisterParams({ from: '2026-01-01', page: '2' }, { page: '2', from: '2026-01-01' })).toBe(true);
    expect(sameExpenseRegisterParams({ from: '2026-01-01' }, { from: '2026-01-01', page: '2' })).toBe(false);
    expect(sameExpenseRegisterParams({ pageSize: '20' }, { pageSize: '50' })).toBe(false);
  });
});
