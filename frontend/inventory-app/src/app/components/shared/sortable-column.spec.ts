import {
  compareSortValues,
  compareSortValuesDirectionalNulls,
  nextSortState,
  sortAriaState,
  sortColumnLabel
} from './sortable-column';

describe('nextSortState', () => {
  it('starts a new column ascending regardless of the previous column\'s direction', () => {
    expect(nextSortState('name', 'desc', 'price')).toEqual({ column: 'price', direction: 'asc' });
  });

  it('toggles the same column from ascending to descending and back', () => {
    expect(nextSortState('name', 'asc', 'name')).toEqual({ column: 'name', direction: 'desc' });
    expect(nextSortState('name', 'desc', 'name')).toEqual({ column: 'name', direction: 'asc' });
  });

  it('starts ascending the first time a column is chosen from no sort at all', () => {
    expect(nextSortState(null, 'asc', 'name')).toEqual({ column: 'name', direction: 'asc' });
  });
});

describe('sortAriaState', () => {
  it('reports none for a column that is not the active sort', () => {
    expect(sortAriaState('name', 'asc', 'price')).toBe('none');
    expect(sortAriaState(null, 'asc', 'price')).toBe('none');
  });

  it('reports ascending or descending for the active sort column', () => {
    expect(sortAriaState('price', 'asc', 'price')).toBe('ascending');
    expect(sortAriaState('price', 'desc', 'price')).toBe('descending');
  });
});

describe('sortColumnLabel', () => {
  it('leaves the label unchanged for a column that is not the active sort', () => {
    expect(sortColumnLabel('Price', 'name', 'asc', 'price')).toBe('Price');
  });

  it('appends the active direction to the label for the active sort column', () => {
    expect(sortColumnLabel('Price', 'price', 'asc', 'price')).toBe('Price (ascending)');
    expect(sortColumnLabel('Price', 'price', 'desc', 'price')).toBe('Price (descending)');
  });
});

describe('compareSortValues', () => {
  it('compares numbers numerically rather than lexicographically', () => {
    expect(compareSortValues(2, 10)).toBeLessThan(0);
    expect(compareSortValues(10, 2)).toBeGreaterThan(0);
  });

  it('compares dates by instant', () => {
    expect(compareSortValues(new Date('2026-01-01'), new Date('2026-02-01'))).toBeLessThan(0);
  });

  it('compares text case-insensitively using locale rules', () => {
    expect(compareSortValues('apple', 'Banana')).toBeLessThan(0);
    expect(compareSortValues('apple', 'APPLE')).toBe(0);
  });
});

describe('compareSortValuesDirectionalNulls', () => {
  it('treats two missing values as equal', () => {
    expect(compareSortValuesDirectionalNulls(null, null)).toBe(0);
  });

  it('sorts a missing value after a present value, and a present value before a missing one', () => {
    expect(compareSortValuesDirectionalNulls(null, 5)).toBeGreaterThan(0);
    expect(compareSortValuesDirectionalNulls(5, null)).toBeLessThan(0);
  });

  it('delegates non-missing comparisons to compareSortValues', () => {
    expect(compareSortValuesDirectionalNulls(2, 10)).toBeLessThan(0);
    expect(compareSortValuesDirectionalNulls('apple', 'Banana')).toBeLessThan(0);
  });
});
