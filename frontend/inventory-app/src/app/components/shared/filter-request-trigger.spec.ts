import { TestScheduler } from 'rxjs/testing';
import { createFilterTrigger$ } from './filter-request-trigger';

/** Small debounce used only to keep marble diagrams readable; production uses SEARCH_DEBOUNCE_MS. */
const TEST_DEBOUNCE_MS = 3;

describe('createFilterTrigger$', () => {
  let scheduler: TestScheduler;

  beforeEach(() => {
    scheduler = new TestScheduler((actual, expected) => expect(actual).toEqual(expected));
  });

  it('debounces rapid consecutive search keystrokes down to the final value', () => {
    scheduler.run(({ cold, expectObservable }) => {
      const search$ = cold('a-b-c-----|', { a: 'p', b: 'pr', c: 'pro' });
      const immediate$ = cold<void>('----------|');

      expectObservable(createFilterTrigger$(search$, immediate$, TEST_DEBOUNCE_MS)).toBe('-------c--|', { c: 'pro' });
    });
  });

  it('ignores an unchanged consecutive search value', () => {
    scheduler.run(({ cold, expectObservable }) => {
      const search$ = cold('a---b---|', { a: 'pro', b: 'pro' });
      const immediate$ = cold<void>('--------|');

      expectObservable(createFilterTrigger$(search$, immediate$, TEST_DEBOUNCE_MS)).toBe('---a----|', { a: 'pro' });
    });
  });

  it('re-emits a search value repeated after an intervening immediate trigger', () => {
    scheduler.run(({ cold, expectObservable }) => {
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
  });

  it('passes immediate (category/supplier) changes through without the search debounce', () => {
    scheduler.run(({ cold, expectObservable }) => {
      const search$ = cold<string>('------------|');
      const immediate$ = cold('-a---b------|', { a: undefined, b: undefined });

      expectObservable(createFilterTrigger$(search$, immediate$)).toBe('-a---b------|', { a: undefined, b: undefined });
    });
  });
});
