import { Injectable, OnDestroy } from '@angular/core';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { EventMessage, EventType, InteractionStatus } from '@azure/msal-browser';
import {
  BehaviorSubject,
  EMPTY,
  Observable,
  Subject,
  catchError,
  defer,
  distinctUntilChanged,
  filter,
  map,
  merge,
  of,
  shareReplay,
  switchMap,
  take,
  takeUntil,
  tap
} from 'rxjs';
import { PlatformDiagnosticsService } from './platform-diagnostics.service';

/**
 * Whether the diagnostics API has answered for the identity the probe was made as.
 *
 * `unknown` and an answered `false` are deliberately different. Nothing has been asked for this
 * identity yet in the first - so a route guard must wait rather than refuse - while in the second
 * the question was asked and the answer was no.
 */
type AnsweredPlatformAccess = { readonly status: 'answered'; readonly granted: boolean };
type PlatformAccessState = { readonly status: 'unknown' } | AnsweredPlatformAccess;

const UNKNOWN: PlatformAccessState = { status: 'unknown' };
const REFUSED: AnsweredPlatformAccess = { status: 'answered', granted: false };
const GRANTED: AnsweredPlatformAccess = { status: 'answered', granted: true };

/**
 * The MSAL events that mean the signed-in identity may have changed. Deliberately the same three
 * `AccessService` watches, and deliberately not read through it: the platform-admin answer has
 * nothing to do with business membership or capabilities, so the two caches share their triggers
 * and nothing else.
 */
const ACCOUNT_EVENTS: ReadonlySet<string> = new Set<string>([
  EventType.LOGIN_SUCCESS,
  EventType.LOGOUT_SUCCESS,
  EventType.ACTIVE_ACCOUNT_CHANGED
]);

/**
 * Whether the application shell should offer the super-admin diagnostics link (issue #335).
 *
 * The answer comes from `GET /api/admin/diagnostics/access` and from nowhere else. There is
 * deliberately no frontend role, claim, flag or configuration value that could say yes: the
 * authority is the separately configured Entra `(tid, oid)` pair the API resolves, and a second
 * source could only diverge from it. A failed, refused or unanswered probe is `false`, and the
 * page and both endpoints stay independently authorized either way - this service decides what a
 * menu shows, never what a caller may read.
 *
 * It waits for MSAL to finish any sign-in interaction and only asks when an account is present.
 * That is not an optimisation: a protected request issued while a redirect is still being handled
 * would have `MsalInterceptor` start an interactive sign-in of its own, on the public `/auth`
 * landing route, for a link nobody has asked for yet.
 *
 * **One coalesced answer per signed-in identity (issue #605).** The probe is shared, so one
 * request serves the sidebar and the route guard however often they ask - but only for the
 * identity it was made as. Signing in, signing out and an MSAL active-account change drop it,
 * observed from the account events and from `inProgress$` settling together, and acted on only
 * when the identity the cache was filled for actually changed, so a duplicate notification costs
 * nothing. An answer from a probe a later one superseded is discarded by a generation check when
 * it arrives, and a superseded probe that has not yet been sent is never sent at all: neither an
 * A to B to C switch nor a sign-out during a pending probe can restore an earlier grant.
 */
@Injectable({ providedIn: 'root' })
export class PlatformDiagnosticsAccessService implements OnDestroy {
  /** The answer for the identity currently signed in, as far as it is known. */
  private readonly state = new BehaviorSubject<PlatformAccessState>(UNKNOWN);

  /** Subscribing starts the first probe, so no consumer has to ask for one itself. */
  private readonly state$: Observable<PlatformAccessState> = defer(() => {
    this.ensureProbe();
    return this.state;
  });

  /** The shared probe since the last invalidation, or `undefined` when nothing has probed. */
  private probing?: Observable<PlatformAccessState>;

  /**
   * The signed-in identity the answer belongs to: `undefined` before anything has been probed, and
   * `null` once it has been probed with nobody signed in.
   */
  private probedForAccountKey?: string | null;

  /** Bumped by every invalidation; a probe from an older generation must not publish its answer. */
  private generation = 0;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly diagnostics: PlatformDiagnosticsService,
    private readonly authService: MsalService,
    private readonly msalBroadcastService: MsalBroadcastService
  ) {
    this.observeSignedInAccount();
  }

  /**
   * Whether the signed-in identity's own probe has confirmed access, now and whenever that
   * changes: `false` until it has, and `false` again the moment the identity changes, so a granted
   * `Super Admin` group is gone on the switch itself rather than when somebody else's answer
   * happens to arrive. This is what the menu renders from.
   */
  readonly granted$: Observable<boolean> = this.state$.pipe(
    map((state) => state.status === 'answered' && state.granted),
    distinctUntilChanged()
  );

  /**
   * Emits once: the answer for the identity signed in when it is asked, waiting for that
   * identity's own probe rather than replaying a previous one's. This is what a route guard needs,
   * because `false` while the answer is still unknown would redirect the configured platform
   * administrator away from a page they may use.
   */
  isGranted(): Observable<boolean> {
    return this.state$.pipe(
      filter((state): state is AnsweredPlatformAccess => state.status === 'answered'),
      map((state) => state.granted),
      take(1)
    );
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  private ensureProbe(): void {
    if (this.probing === undefined) {
      this.beginProbe();
    }
  }

  private beginProbe(): void {
    const generation = ++this.generation;

    const probing = this.msalBroadcastService.inProgress$.pipe(
      filter((status: InteractionStatus) => status === InteractionStatus.None),
      take(1),
      switchMap(() => {
        if (generation !== this.generation) {
          // A later probe owns the answer now, so this one's request would only ask about an
          // identity nobody is waiting for - while still being a protected request.
          return EMPTY;
        }

        const accountKey = this.activeAccountKey();
        this.probedForAccountKey = accountKey;
        // Nobody signed in is an answer, not a request: an unauthenticated probe would only have
        // MsalInterceptor start an interactive sign-in nobody asked for.
        return accountKey === null ? of(REFUSED) : this.probe();
      }),
      tap((answer) => this.publish(generation, answer)),
      shareReplay({ bufferSize: 1, refCount: false })
    );

    this.probing = probing;
    // Subscribed here, not by the caller: the answer has to be published for the menu whether or
    // not anybody is listening to this particular probe.
    probing.pipe(takeUntil(this.destroying$)).subscribe();
  }

  private probe(): Observable<AnsweredPlatformAccess> {
    return this.diagnostics.access().pipe(
      map((access) => (access.authorized === true ? GRANTED : REFUSED)),
      // A refusal is the expected answer for everyone except one configured identity, so it is
      // not reported to the operator and not logged: the link simply does not appear.
      catchError(() => of(REFUSED))
    );
  }

  private publish(generation: number, answer: PlatformAccessState): void {
    if (generation !== this.generation) {
      // A later invalidation owns the answer now; this one was asked as a different identity.
      return;
    }

    this.state.next(answer);
  }

  /**
   * Sign-in, sign-out and an active-account change, from the two signals that report them: MSAL's
   * own account events, and `inProgress$` settling, which is what the shell already uses to decide
   * that an interaction has finished.
   */
  private observeSignedInAccount(): void {
    merge(
      this.msalBroadcastService.inProgress$.pipe(
        filter((status: InteractionStatus) => status === InteractionStatus.None)
      ),
      this.msalBroadcastService.msalSubject$.pipe(
        filter((event: EventMessage) => ACCOUNT_EVENTS.has(event.eventType))
      )
    )
      .pipe(takeUntil(this.destroying$))
      .subscribe(() => this.applySignedInAccount());
  }

  private applySignedInAccount(): void {
    if (this.probedForAccountKey === undefined) {
      // Nothing has probed yet, so the first probe will ask as whoever is signed in then.
      return;
    }

    const accountKey = this.activeAccountKey();
    if (accountKey === this.probedForAccountKey) {
      return;
    }

    if (accountKey === null) {
      this.signedOut();
      return;
    }

    this.probeAgain();
  }

  /**
   * Nobody is signed in, which is an answer rather than something to ask about: the grant goes
   * immediately, a probe still in flight can no longer publish one, and no further request is
   * made. The next consumer that asks probes as whoever is signed in then.
   */
  private signedOut(): void {
    this.generation++;
    this.probing = undefined;
    this.probedForAccountKey = null;
    this.state.next(REFUSED);
  }

  /**
   * Somebody else is signed in now. The answer becomes unknown before the new probe starts, so the
   * menu drops a previous grant at once while a guard waits for this identity's own answer instead
   * of refusing on a `false` that is only "not asked yet".
   */
  private probeAgain(): void {
    this.state.next(UNKNOWN);
    this.beginProbe();
  }

  /** The stable identity of the signed-in account, independent of its display name. */
  private activeAccountKey(): string | null {
    const account =
      this.authService.instance.getActiveAccount() ?? this.authService.instance.getAllAccounts()[0] ?? null;
    if (account === null) {
      return null;
    }

    return account.homeAccountId || account.localAccountId || account.username || '';
  }
}
