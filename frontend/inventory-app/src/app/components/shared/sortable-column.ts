export type SortDirection = 'asc' | 'desc';
export type SortValue = string | number | Date | null;

/** Click/Enter on a column header: same column toggles direction, a new column starts ascending. */
export function nextSortState<TColumn>(
  currentColumn: TColumn | null,
  currentDirection: SortDirection,
  requestedColumn: TColumn
): { column: TColumn; direction: SortDirection } {
  if (currentColumn === requestedColumn) {
    return { column: requestedColumn, direction: currentDirection === 'asc' ? 'desc' : 'asc' };
  }

  return { column: requestedColumn, direction: 'asc' };
}

export function sortAriaState<TColumn>(
  currentColumn: TColumn | null,
  currentDirection: SortDirection,
  column: TColumn
): 'ascending' | 'descending' | 'none' {
  if (currentColumn !== column) {
    return 'none';
  }

  return currentDirection === 'asc' ? 'ascending' : 'descending';
}

export function sortColumnLabel<TColumn>(
  label: string,
  currentColumn: TColumn | null,
  currentDirection: SortDirection,
  column: TColumn
): string {
  if (currentColumn !== column) {
    return label;
  }

  return `${label} (${currentDirection === 'asc' ? 'ascending' : 'descending'})`;
}

/** Compares two non-missing values: numbers and dates compare numerically, everything else as locale-aware text. */
export function compareSortValues(left: NonNullable<SortValue>, right: NonNullable<SortValue>): number {
  if (left instanceof Date && right instanceof Date) {
    return left.getTime() - right.getTime();
  }
  if (typeof left === 'number' && typeof right === 'number') {
    return left - right;
  }

  return String(left).localeCompare(String(right), undefined, { sensitivity: 'base' });
}

/** Missing values sort last on ascending and first on descending; non-missing values use {@link compareSortValues}. */
export function compareSortValuesDirectionalNulls(left: SortValue, right: SortValue): number {
  if (left == null && right == null) {
    return 0;
  }
  if (left == null) {
    return 1;
  }
  if (right == null) {
    return -1;
  }

  return compareSortValues(left, right);
}
