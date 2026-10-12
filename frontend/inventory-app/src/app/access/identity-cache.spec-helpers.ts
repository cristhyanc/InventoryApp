import { HttpClient } from '@angular/common/http';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, EventMessage, EventType, InteractionStatus } from '@azure/msal-browser';
import { BehaviorSubject, Observable, Subject, of } from 'rxjs';
import { ConfigService } from '../services/config.service';

/**
 * The doubles and the shared identity-change contract for the two caches that answer for the
 * signed-in identity: `AccessService` (issue #528) and `AccountStateService` (issue #529).
 *
 * The two services are deliberate siblings rather than a shared base, so the rule issue #603 added
 * - the identity signed in now owns the answer, from the moment it changes - is written once here
 * and asserted against both, instead of twice in two spec files that would then drift. The doubles
 * are shared for the same reason: a guard spec, the sidebar's spec and the supplier GST editor's
 * spec all exercise the real service with only its one `GET` and MSAL's two signals replaced, and
 * that arrangement is one thing, not four copies of one thing.
 *
 * This file is test-only (`tsconfig.spec.json` compiles it; the application build never does).
 */

/** The signed-in account MSAL reports, identified the way the services read it. */
export function account(homeAccountId: string): AccountInfo {
  return { homeAccountId, username: `${homeAccountId}@example.test` } as unknown as AccountInfo;
}

/** MSAL as the two caches observe it: who is signed in, and the signals that say it changed. */
export interface SignedInIdentityDoubles {
  readonly msal: MsalService;
  readonly broadcast: MsalBroadcastService;
  /** MSAL's interaction status, so a test can hold a read until an interaction settles. */
  readonly inProgress$: BehaviorSubject<InteractionStatus>;
  /** Changes who MSAL reports as signed in, without announcing it. */
  signIn(accountId: string | null): void;
  /** Raises one MSAL account event. */
  raise(eventType: EventType): void;
  /** Both halves of switching Microsoft account: a different active account, announced. */
  switchAccount(accountId: string): void;
}

export function signedInIdentity(
  options: { signedIn?: string | null; interaction?: InteractionStatus } = {}
): SignedInIdentityDoubles {
  let signedIn = options.signedIn === undefined ? 'member-1' : options.signedIn;
  const inProgress$ = new BehaviorSubject(options.interaction ?? InteractionStatus.None);
  const msalSubject$ = new Subject<EventMessage>();
  const raise = (eventType: EventType): void => msalSubject$.next({ eventType } as EventMessage);

  return {
    msal: {
      instance: {
        getActiveAccount: () => (signedIn === null ? null : account(signedIn)),
        getAllAccounts: () => (signedIn === null ? [] : [account(signedIn)])
      }
    } as unknown as MsalService,
    broadcast: { inProgress$, msalSubject$ } as unknown as MsalBroadcastService,
    inProgress$,
    signIn: (accountId) => {
      signedIn = accountId;
    },
    raise,
    switchAccount: (accountId) => {
      signedIn = accountId;
      raise(EventType.ACTIVE_ACCOUNT_CHANGED);
    }
  };
}

/** The `HttpClient` double: each cache issues exactly one `GET`, answered by `read`. */
export function readingHttp(read: () => unknown): HttpClient {
  return { get: read } as unknown as HttpClient;
}

/** The API base URL both caches build their one URL from. */
export function apiConfig(): ConfigService {
  return { apiBaseUrl: '/api' } as unknown as ConfigService;
}

/**
 * A `GET` double that answers `responses` in the order the reads are issued, and `fallback` for
 * every read after them - which is how a test holds one identity's read pending while another's
 * answers.
 */
export function queuedReads(responses: readonly (() => unknown)[], fallback: unknown): jest.Mock {
  const queued = [...responses];
  return jest.fn(() => {
    const next = queued.shift();
    return next === undefined ? of(fallback) : next();
  });
}

/** What the shared suite needs of the cache under test: its two re-read entry points. */
export interface IdentityCache<TAnswer> {
  refresh(): Observable<TAnswer>;
  refreshUnlessReading(): void;
}

/** One configured cache, with the MSAL signals and the read that answer for it. */
export interface IdentityCacheHarness<TService, TAnswer> {
  readonly service: TService;
  /** Every read the cache issued. */
  readonly get: jest.Mock;
  readonly inProgress$: BehaviorSubject<InteractionStatus>;
  signIn(accountId: string | null): void;
  raise(eventType: EventType): void;
  /** Starts recording what the cache publishes, which also triggers the first read. */
  observe(): TAnswer[];
}

/** How one cache answers the questions the shared identity-change suite asks of both. */
export interface IdentityCacheContract<
  TService extends IdentityCache<TAnswer>,
  TAnswer extends { readonly status: string }
> {
  /** The cache under test, named in the suite. */
  readonly subject: string;
  /** The status it publishes once it has an answer: `granted` on one cache, `known` on the other. */
  readonly answered: string;
  /** The first identity's response body, and the second identity's different one. */
  readonly firstResponse: unknown;
  readonly secondResponse: unknown;
  configure(responses: (() => unknown)[]): IdentityCacheHarness<TService, TAnswer>;
  /**
   * Subscribes the way a guard does - waiting for the first answer that is not "nothing has asked
   * yet" - and asserts it is the one {@link secondResponse} reported. It is called before that
   * response arrives, so it must subscribe synchronously.
   */
  guardAnswer(harness: IdentityCacheHarness<TService, TAnswer>): Promise<void>;
}

/**
 * The identity-change contract both caches obey (issue #603): an actual change of signed-in
 * identity drops the previous identity's answer before the new read is issued or even waited for,
 * a superseded read can neither publish nor look like the current read finishing, a sign-out
 * clears the answer without reading again, and a same-identity re-read keeps its own answer.
 */
export function describeIdentityChange<
  TService extends IdentityCache<TAnswer>,
  TAnswer extends { readonly status: string }
>(contract: IdentityCacheContract<TService, TAnswer>): void {
  const statuses = (answers: readonly TAnswer[]): string[] => answers.map((answer) => answer.status);

  describe(`${contract.subject} identity change (issue #603)`, () => {
    it('drops the previous identity’s answer before the new identity’s read answers', () => {
      const pending = new Subject<unknown>();
      const harness = contract.configure([() => of(contract.firstResponse), () => pending]);
      const observed = harness.observe();
      expect(statuses(observed)).toEqual([contract.answered]);

      harness.signIn('member-2');
      harness.raise(EventType.ACTIVE_ACCOUNT_CHANGED);

      expect(statuses(observed)).toEqual([contract.answered, 'unread']);

      pending.next(contract.secondResponse);
      pending.complete();

      expect(statuses(observed)).toEqual([contract.answered, 'unread', contract.answered]);
    });

    it('answers a subscriber that arrives while the new read is pending from the new identity', async () => {
      const pending = new Subject<unknown>();
      const harness = contract.configure([() => of(contract.firstResponse), () => pending]);
      harness.observe();

      harness.signIn('member-2');
      harness.raise(EventType.ACTIVE_ACCOUNT_CHANGED);
      const decided = contract.guardAnswer(harness);

      pending.next(contract.secondResponse);
      pending.complete();

      await decided;
    });

    it('publishes nothing for an identity two switches ago, and reads each switch once', async () => {
      const first = new Subject<unknown>();
      const second = new Subject<unknown>();
      const harness = contract.configure([() => first, () => second, () => of(contract.secondResponse)]);
      const observed = harness.observe();

      harness.signIn('member-2');
      harness.raise(EventType.ACTIVE_ACCOUNT_CHANGED);
      harness.signIn('member-3');
      harness.raise(EventType.ACTIVE_ACCOUNT_CHANGED);
      expect(harness.get).toHaveBeenCalledTimes(3);

      first.next(contract.firstResponse);
      first.complete();
      second.next(contract.firstResponse);
      second.complete();

      expect(statuses(observed)).toEqual(['unread', contract.answered]);
      await contract.guardAnswer(harness);
    });

    /** A late answer must not look like the new identity's read finishing, either. */
    it('leaves the new identity’s read pending when the previous identity answers last', () => {
      const stale = new Subject<unknown>();
      const current = new Subject<unknown>();
      const harness = contract.configure([() => stale, () => current]);
      const observed = harness.observe();

      harness.signIn('member-2');
      harness.raise(EventType.ACTIVE_ACCOUNT_CHANGED);
      expect(harness.get).toHaveBeenCalledTimes(2);

      stale.next(contract.firstResponse);
      stale.complete();
      harness.service.refreshUnlessReading();

      expect(harness.get).toHaveBeenCalledTimes(2);
      expect(statuses(observed)).toEqual(['unread']);
    });

    /**
     * One sign-in reports itself twice - MSAL raises its account event and `inProgress$` settles -
     * and neither may become a second read of the same identity.
     */
    it('issues one read when an account event and the settling interaction report one change', () => {
      const harness = contract.configure([]);
      harness.observe();
      expect(harness.get).toHaveBeenCalledTimes(1);

      harness.inProgress$.next(InteractionStatus.HandleRedirect);
      harness.signIn('member-2');
      harness.raise(EventType.ACTIVE_ACCOUNT_CHANGED);
      expect(harness.get).toHaveBeenCalledTimes(1);

      harness.inProgress$.next(InteractionStatus.None);

      expect(harness.get).toHaveBeenCalledTimes(2);
    });

    it('clears the answer and the pending read on a sign-out, without reading again', () => {
      const pending = new Subject<unknown>();
      const harness = contract.configure([() => of(contract.firstResponse), () => pending]);
      const observed = harness.observe();
      harness.service.refresh();

      harness.signIn(null);
      harness.raise(EventType.LOGOUT_SUCCESS);
      pending.next(contract.firstResponse);
      pending.complete();

      expect(harness.get).toHaveBeenCalledTimes(2);
      expect(statuses(observed)).toEqual([contract.answered, 'unread']);
    });

    /** The explicit refresh is unchanged: a same-identity re-read keeps its own answer meanwhile. */
    it('keeps its own answer published while a same-identity refresh is in flight', () => {
      const pending = new Subject<unknown>();
      const harness = contract.configure([() => of(contract.firstResponse), () => pending]);
      const observed = harness.observe();

      harness.service.refresh();
      expect(statuses(observed)).toEqual([contract.answered]);

      pending.next(contract.secondResponse);
      pending.complete();

      expect(statuses(observed)).toEqual([contract.answered, contract.answered]);
    });
  });
}
