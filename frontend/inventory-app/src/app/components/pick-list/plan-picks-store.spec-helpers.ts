import { Provider } from '@angular/core';
import { BehaviorSubject, Observable, Subject, of } from 'rxjs';
import { PlanPicksRestoration, PlanPicksStoreService } from './plan-picks-store.service';
import { StoredPlanPicks } from './plan-picks-storage';

/** How a spec wants this tab's Plan picks storage to behave (issue #1021). */
export interface PlanPicksStoreDoubleOptions {
  /** A plan already held for this tab, which the page restores when it opens. */
  readonly stored?: StoredPlanPicks | null;

  /** Whether writes succeed. `false` is a tab whose storage refuses them (private mode, a quota). */
  readonly writable?: boolean;

  /**
   * Whether the scope a stored plan was read under is still the one in effect when it is applied -
   * and, as in the real service, whether a plan may be written under it at all.
   */
  readonly scopeCurrent?: boolean;

  /**
   * When the stored-plan read answers: at once (the default), only when the spec says so
   * ({@link PlanPicksStoreDouble.answerRestore}), or never - a tab whose signed-in context is still
   * being resolved.
   */
  readonly restore?: 'immediately' | 'deferred' | 'never';
}

/** An in-memory stand-in for this tab's Plan picks storage, and what a spec can ask it afterwards. */
export interface PlanPicksStoreDouble {
  readonly service: PlanPicksStoreService;

  /** The plan the page has stored, or `null` when it has stored none or cleared it. */
  stored(): StoredPlanPicks | null;

  /** How many times the page has asked for the stored plan to be removed. */
  clears(): number;

  /** How many times the page has written a plan, refused writes included. */
  writes(): number;

  /** Answers a `'deferred'` stored-plan read with whatever the double now holds. */
  answerRestore(): void;

  /**
   * Publishes a changed scope: another signed-in account, another business, or a started support
   * session or role preview (`eligible: false`, which also refuses every write).
   */
  changeScope(options?: { eligible?: boolean }): void;
}

/**
 * A {@link PlanPicksStoreService} backed by one in-memory plan, for the specs of the pages and
 * views around it: the real service resolves the signed-in identity, the business and the support
 * context through MSAL and three other services, none of which a Pick List spec is about.
 * `plan-picks-store.service.spec.ts` exercises the real one, and `plan-picks-storage.spec.ts` the
 * `sessionStorage` access and validation behind it.
 */
export function planPicksStoreDouble(options: PlanPicksStoreDoubleOptions = {}): PlanPicksStoreDouble {
  const writable = options.writable ?? true;
  const scopeCurrent = options.scopeCurrent ?? true;
  const when = options.restore ?? 'immediately';
  const deferred = new Subject<PlanPicksRestoration | null>();
  let stored: StoredPlanPicks | null = options.stored ?? null;
  let clears = 0;
  let writes = 0;
  /** The scope in effect, as the real service publishes it, and whether a plan may be kept in it. */
  const scopeToken = new BehaviorSubject(1);
  let eligible = true;
  const isCurrentScope = (token: number): boolean => scopeCurrent && eligible && token === scopeToken.value;
  const answer = (): PlanPicksRestoration | null => (stored === null ? null : { scopeToken: scopeToken.value, plan: stored });

  const service = {
    restore: (): Observable<PlanPicksRestoration | null> => {
      if (when === 'never') {
        // Never answers: a tab whose signed-in context nobody has resolved.
        return new Observable<PlanPicksRestoration | null>();
      }
      return when === 'deferred' ? deferred.asObservable() : of(answer());
    },
    scopeToken$: scopeToken.asObservable(),
    isCurrentScope,
    save: (plan: StoredPlanPicks, token: number): boolean => {
      writes++;
      // The real service refuses a plan whose scope is no longer the one in effect, however
      // writable the storage is.
      if (!writable || !isCurrentScope(token)) {
        return false;
      }
      stored = plan;
      return true;
    },
    clear: (): void => {
      clears++;
      stored = null;
    }
  } as unknown as PlanPicksStoreService;

  return {
    service,
    stored: () => stored,
    clears: () => clears,
    writes: () => writes,
    answerRestore: () => {
      deferred.next(answer());
      deferred.complete();
    },
    changeScope: (options: { eligible?: boolean } = {}) => {
      eligible = options.eligible ?? true;
      scopeToken.next(scopeToken.value + 1);
    }
  };
}

/** The same double as a `TestBed` provider, for a spec that renders the Pick List page. */
export function providePlanPicksStore(double: PlanPicksStoreDouble = planPicksStoreDouble()): Provider {
  return { provide: PlanPicksStoreService, useValue: double.service };
}
