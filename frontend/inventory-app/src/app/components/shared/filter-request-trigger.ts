import { Observable, defer, merge } from 'rxjs';
import { debounceTime, filter, tap } from 'rxjs/operators';

/**
 * Debounce applied to free-text search input before it can trigger a request.
 * Short enough to feel responsive, long enough to skip most in-progress keystrokes.
 */
export const SEARCH_DEBOUNCE_MS = 300;

/** Marks "no search value has been applied since the last immediate trigger". */
const NO_APPLIED_SEARCH = Symbol('no-applied-search');

/**
 * Combines a debounced, change-only search stream with an immediate filter
 * stream into a single request-trigger stream. Category/supplier-style
 * filters emit on `immediate$` and are not delayed by the search debounce.
 *
 * Deduplication applies only to truly consecutive unchanged search states: an
 * `immediate$` trigger (category/supplier change, reset, post-mutation refresh)
 * clears the remembered search value, so repeating an earlier search term after
 * one of those refreshes still fires a request.
 */
export function createFilterTrigger$<T>(
  search$: Observable<T>,
  immediate$: Observable<void>,
  debounceMs: number = SEARCH_DEBOUNCE_MS
): Observable<T | void> {
  return defer(() => {
    let appliedSearch: T | typeof NO_APPLIED_SEARCH = NO_APPLIED_SEARCH;

    return merge(
      search$.pipe(
        debounceTime(debounceMs),
        filter(value => value !== appliedSearch),
        tap(value => (appliedSearch = value))
      ),
      immediate$.pipe(tap(() => (appliedSearch = NO_APPLIED_SEARCH)))
    );
  });
}
