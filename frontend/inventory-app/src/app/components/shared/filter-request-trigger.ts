import { Observable, merge } from 'rxjs';
import { debounceTime, distinctUntilChanged } from 'rxjs/operators';

/**
 * Debounce applied to free-text search input before it can trigger a request.
 * Short enough to feel responsive, long enough to skip most in-progress keystrokes.
 */
export const SEARCH_DEBOUNCE_MS = 300;

/**
 * Combines a debounced, change-only search stream with an immediate filter
 * stream into a single request-trigger stream. Category/supplier-style
 * filters emit on `immediate$` and are not delayed by the search debounce.
 */
export function createFilterTrigger$<T>(
  search$: Observable<T>,
  immediate$: Observable<void>,
  debounceMs: number = SEARCH_DEBOUNCE_MS
): Observable<T | void> {
  return merge(search$.pipe(debounceTime(debounceMs), distinctUntilChanged()), immediate$);
}
