/**
 * Client-side paging over a list that has already been read in full (issue #1093, after the
 * Dashboard's Reorder priorities, issues #1017 and #1080). Every function here is pure: a page is a
 * window onto the list, never a second read, so turning a page or choosing a size can neither drop
 * nor repeat an item.
 */

/** How many pages `total` items fill at `size` per page. An empty list is still one (empty) page. */
export function pageCount(total: number, size: number): number {
  if (size <= 0) {
    return 1;
  }
  return Math.max(1, Math.ceil(total / size));
}

/**
 * The page index to show once the list may have changed length: the same page while it still
 * exists, the new last page when the list has shrunk past it, and never below the first.
 */
export function clampPageIndex(index: number, total: number, size: number): number {
  return Math.min(Math.max(0, index), pageCount(total, size) - 1);
}

/** The items on page `index` (0-based) at `size` per page. */
export function pageSlice<T>(items: readonly T[], index: number, size: number): T[] {
  const page = clampPageIndex(index, items.length, size);
  return items.slice(page * size, page * size + size);
}

/**
 * The 1-based range of items page `index` shows, for "Showing 11-20 of 71". An empty list shows
 * the range `0-0`, which is how the label says there is nothing rather than "1-0".
 */
export function pageRange(index: number, total: number, size: number): { first: number; last: number } {
  if (total === 0) {
    return { first: 0, last: 0 };
  }
  const page = clampPageIndex(index, total, size);
  return { first: page * size + 1, last: Math.min(total, page * size + size) };
}
