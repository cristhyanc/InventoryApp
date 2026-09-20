import * as assert from 'assert';
import { TestScheduler } from 'rxjs/testing';
import { createFilterTrigger$ } from './filter-request-trigger';

/** Small debounce used only to keep marble diagrams readable; production uses SEARCH_DEBOUNCE_MS. */
const TEST_DEBOUNCE_MS = 3;

/**
 * This repository has no configured Angular/Jasmine/Jest test runner (see
 * AGENTS.md and CLAUDE.md), so this file is not wired into any npm script
 * and is not part of `bash scripts/validate.sh`. It is a deterministic,
 * framework-free verification of the debounce/merge contract behind issue
 * #41, using RxJS's own `TestScheduler` (virtual time) and Node's built-in
 * `assert`. Run it manually from the repository root with the `jiti` runner
 * that is already present in the frontend's dependency tree:
 *
 *   npm --prefix frontend/inventory-app ci
 *   npm --prefix frontend/inventory-app exec -- jiti \
 *     frontend/inventory-app/src/app/components/shared/filter-request-trigger.spec.ts
 *
 * It prints one `PASS:` line per check and exits non-zero on the first failure.
 */

function runScheduler(name: string, run: (helpers: Parameters<TestScheduler['run']>[0] extends (h: infer H) => unknown ? H : never) => void): void {
  const scheduler = new TestScheduler((actual, expected) => assert.deepStrictEqual(actual, expected));
  scheduler.run(run);
  // eslint-disable-next-line no-console
  console.log(`PASS: ${name}`);
}

runScheduler('debounces rapid consecutive search keystrokes down to the final value', ({ cold, expectObservable }) => {
  const search$ = cold('a-b-c-----|', { a: 'p', b: 'pr', c: 'pro' });
  const immediate$ = cold<void>('----------|');

  expectObservable(createFilterTrigger$(search$, immediate$, TEST_DEBOUNCE_MS)).toBe('-------c--|', { c: 'pro' });
});

runScheduler('ignores an unchanged consecutive search value', ({ cold, expectObservable }) => {
  const search$ = cold('a---b---|', { a: 'pro', b: 'pro' });
  const immediate$ = cold<void>('--------|');

  expectObservable(createFilterTrigger$(search$, immediate$, TEST_DEBOUNCE_MS)).toBe('---a----|', { a: 'pro' });
});

runScheduler('re-emits a search value repeated after an intervening immediate trigger', ({ cold, expectObservable }) => {
  // "coffee" is applied, Reset Filters (or a category change) refreshes immediately,
  // then "coffee" is typed again: the second search must still reach the API.
  const search$ = cold('a-----b---|', { a: 'coffee', b: 'coffee' });
  const immediate$ = cold('----i-----|', { i: undefined });

  expectObservable(createFilterTrigger$(search$, immediate$, TEST_DEBOUNCE_MS)).toBe('---xy----z|', {
    x: 'coffee',
    y: undefined,
    z: 'coffee'
  });
});

runScheduler('passes immediate (category/supplier) changes through without the search debounce', ({ cold, expectObservable }) => {
  const search$ = cold<string>('------------|');
  const immediate$ = cold('-a---b------|', { a: undefined, b: undefined });

  expectObservable(createFilterTrigger$(search$, immediate$)).toBe('-a---b------|', { a: undefined, b: undefined });
});

// eslint-disable-next-line no-console
console.log('All filter-request-trigger checks passed.');
