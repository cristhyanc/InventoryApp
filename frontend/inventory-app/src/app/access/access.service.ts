import { HttpClient } from '@angular/common/http';
import { Injectable, OnDestroy } from '@angular/core';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { InteractionStatus } from '@azure/msal-browser';
import {
  BehaviorSubject,
  Observable,
  Subject,
  catchError,
  defer,
  distinctUntilChanged,
  filter,
  map,
  of,
  shareReplay,
  skipWhile,
  switchMap,
  take,
  takeUntil,
  tap
} from 'rxjs';
import { ConfigService } from '../services/config.service';
import { Capability, CapabilitySet, capabilitySet } from './capabilities';
import { OptionVisibility } from './option-visibility';
import { activeAccountKey, signedInAccountMayHaveChanged } from './signed-in-account';

/** The `GET /api/me/access` response body (issue #521). */
interface MeAccessResponse {
  role: string | null;
  capabilities: string[];
  businessName: string;
  timeZoneId: string;

  /**
   * The shared switchable option visibility (issue #873). Optional here deliberately: an API that
   * does not report it is read as "not known", which hides nothing - see {@link OptionVisibility}.
   */
  historicalGstClassificationVisible?: boolean;
}

/** What the signed-in member may do, as `GET /api/me/access` reported it. */
export interface MemberAccess {
  /**
   * The member's own role name - `Owner`, `Manager` or `Operator` - for display only, and `null`
   * inside a support session, where the caller holds no membership in the business being viewed
   * and the API reports none (issue #551). It is never a capability source: what may be done comes
   * from {@link capabilities} alone.
   */
  readonly role: string | null;
  readonly capabilities: CapabilitySet;
  readonly businessName: string;
  readonly timeZoneId: string;

  /**
   * Which switchable options the application offers (issue #873), or `undefined` when the API did
   * not report it. It travels with the capabilities because the shell builds one menu from both,
   * and it is **not** one of them: it says what is offered, never what this member may do. See
   * {@link OptionVisibility}.
   */
  readonly optionVisibility?: OptionVisibility;
}

/**
 * Whether the member's access is known yet.
 *
 * `unread` and `unavailable` are deliberately different answers. Nothing has been asked yet in the
 * first; in the second the question was asked and could not be answered - nobody is signed in, or
 * the request failed - and the application must not keep waiting for it.
 */
export type MemberAccessState =
  | { readonly status: 'unread' }
  | { readonly status: 'granted'; readonly access: MemberAccess }
  | { readonly status: 'unavailable' };

const UNREAD: MemberAccessState = { status: 'unread' };
const UNAVAILABLE: MemberAccessState = { status: 'unavailable' };

/**
 * Whether `state` grants at least one of `capabilities`, or `undefined` while nothing has been
 * read: "not known yet" is not the same answer as "not granted", and only the caller knows which
 * of the two it can act on. An unreadable capability set grants it - the fail-open rule documented
 * on {@link AccessService}.
 */
function grantedBy(state: MemberAccessState, capabilities: readonly Capability[]): boolean | undefined {
  switch (state.status) {
    case 'unread':
      return undefined;
    case 'granted':
      return capabilities.some((capability) => state.access.capabilities.has(capability));
    default:
      return true;
  }
}

/**
 * What the signed-in member may do, read once from `GET /api/me/access` (issue #521) and cached
 * for the application shell, the navigation, the route guards and the pages that hide an action a
 * role cannot use (issue #528).
 *
 * **It decides what is rendered, never what may be read.** Every capability here is also checked
 * by the endpoint behind the page, which refuses the request itself with a bare `403` (see
 * docs/architecture.md § Capability authorization). A hidden menu entry, a redirected route and a
 * hidden button therefore hide a page rather than protect one, and nothing in the browser - no
 * role, claim, flag or stored value - is a second source for this answer.
 *
 * **One cached answer per signed-in identity.** The read is issued lazily, when something first
 * asks, and only once MSAL has finished any sign-in interaction: a protected request issued while
 * a redirect is still being handled would have `MsalInterceptor` start an interactive sign-in of
 * its own on the public `/auth` landing route. The cache is dropped or read again on each of the
 * triggers issue #528 lists:
 *
 * - **sign-in, sign-out and an MSAL active-account change** - observed from `inProgress$` settling
 *   and from the three MSAL account events, and acted on only when the signed-in identity the
 *   cache was read for actually changed. A sign-out clears it without reading again, because
 *   there is nobody to read it for;
 *
 *   **A changed identity owns the answer from the moment it changes** (issue #603). The previous
 *   identity's capabilities are dropped before the new read is issued or even waited for, so the
 *   published answer goes back to `unread` and stays there until the new identity's own answer
 *   arrives: a guard activated in that window waits for it, the shell renders no capability at
 *   all rather than the previous member's, and a page already rendering an action is told the
 *   grant is withdrawn - {@link allowsAny} reports `false` for the window, because a subscriber
 *   that received `true` can be corrected only by another answer. The cache then belongs to no
 *   identity until the new read reaches one, which is what makes the account event and the
 *   settling interaction that both report one change read it once. An answer from the superseded
 *   read is discarded whenever it arrives - including after rapid switches - and cannot clear the
 *   current read's pending state either. A same-identity re-read (an explicit {@link refresh}, a
 *   `403`, a stale window) keeps its own answer published while it runs, because it is re-reading
 *   the same person's capabilities;
 * - **any `403` from a member endpoint** - the member's role may have changed since the cache was
 *   filled, so `accessForbiddenInterceptor` calls {@link refreshUnlessReading}. A `403` from
 *   `GET /api/me/access` itself is excluded there, so a refusal can never drive a refresh loop;
 * - **window focus more than {@link staleAfterMs} after the last read** - a tab left open all day
 *   must not keep rendering a menu built from a role somebody has since changed;
 * - **an explicit {@link refresh}**, which is how a page that has just changed something relevant
 *   (a role, a membership) makes the shell re-render from the new capabilities.
 *
 * **A read that cannot be answered hides nothing.** `unavailable` leaves every menu entry, route
 * and action exactly as it was before capabilities were rendered at all: the API is the boundary,
 * so failing closed here would lock a legitimate member out of a page over a failed menu probe,
 * while failing open shows an entry whose endpoint still refuses what the role may not do. Which
 * screen a person with no usable membership is shown at all is issue #529's account-state routing.
 */
@Injectable({ providedIn: 'root' })
export class AccessService implements OnDestroy {
  /** Window focus this long after the last read re-reads the capabilities. */
  static readonly staleAfterMs = 5 * 60 * 1000;

  private readonly state = new BehaviorSubject<MemberAccessState>(UNREAD);

  /** The shared read since the last invalidation, or `undefined` when nothing has been read. */
  private reading?: Observable<MemberAccessState>;

  /** True while {@link reading} has not yet produced an answer. */
  private isReading = false;

  /** `Date.now()` of the last answered read; `null` when there is no answer to go stale. */
  private answeredAtMs: number | null = null;

  /**
   * The signed-in identity the cache belongs to, and `null` once it belongs to nobody signed in.
   *
   * It is `undefined` while the cache belongs to no identity at all: before anything has been
   * read, and while the read a changed identity started is still waiting for MSAL's interaction to
   * settle, because that read belongs to whoever is signed in once it does. Nothing is published
   * in either case, so a further account notification arriving meanwhile has nothing to
   * invalidate and starts no second read of the same identity.
   */
  private cachedForAccountKey?: string | null;

  /** Bumped by every invalidation; a read from an older generation must not publish its answer. */
  private generation = 0;

  private readonly destroying$ = new Subject<void>();
  private readonly onWindowFocus = (): void => this.refreshIfStale();

  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService,
    private readonly authService: MsalService,
    private readonly msalBroadcastService: MsalBroadcastService
  ) {
    this.observeSignedInAccount();
    window.addEventListener('focus', this.onWindowFocus);
  }

  /**
   * The member's access, now and whenever it changes. Subscribing reads it if nothing has read it
   * yet, so a consumer never has to trigger the read itself; `unread` is emitted until the first
   * answer arrives, so a caller can tell "not known yet" from "known to be unavailable".
   */
  readonly access$: Observable<MemberAccessState> = defer(() => {
    this.ensureRead();
    return this.state;
  });

  /**
   * Whether the member's role grants `capability`, now and whenever the access changes. An
   * unreadable capability set answers `true`: see the fail-open rule on this class.
   */
  allows(capability: Capability): Observable<boolean> {
    return this.allowsAny([capability]);
  }

  /**
   * Whether the member's role grants at least one of `capabilities` - what a page reachable
   * through several of them (the Admin hub) needs. An empty list is granted by nothing.
   *
   * **It waits for the first answer, and withdraws a grant the identity change takes away**
   * (issue #603). Nothing is emitted while the capabilities have never been read, so a guard
   * activating then waits for the answer instead of being refused a page their own role may well
   * allow. Once an answer has been published, a return to `unread` is an identity change: the
   * answer belonged to somebody else, so the grant is reported as withdrawn until the new
   * identity's own answer arrives. A subscriber that is already rendering the action cannot be
   * told any other way, and leaving the previous member's `true` as the last thing it heard is
   * what kept the supplier GST editor's Save control enabled for the next member's role.
   */
  allowsAny(capabilities: readonly Capability[]): Observable<boolean> {
    return this.access$.pipe(
      map((state) => grantedBy(state, capabilities)),
      skipWhile((granted) => granted === undefined),
      map((granted) => granted ?? false),
      distinctUntilChanged()
    );
  }

  /**
   * Re-reads the capabilities, discarding whatever is cached, and emits the answer.
   *
   * This is the explicit refresh other work calls when it knows something relevant changed, and
   * it is unconditional: an answer still in flight may have been issued as a different identity,
   * so reusing it is not safe here. An answer from a read this one supersedes is discarded when it
   * arrives.
   */
  refresh(): Observable<MemberAccessState> {
    return this.beginRead();
  }

  /**
   * Re-reads the capabilities unless a read is already in flight, whose answer reports the
   * server's current capabilities anyway.
   *
   * This is what the "`403` from a member endpoint" and "window focus" triggers use. Both can
   * arrive many times in a moment - a page whose every request is refused, a tab focused twice -
   * and neither is asking for anything a read already running will not report, so coalescing them
   * is what keeps one refusal from becoming one capability read per refused request.
   */
  refreshUnlessReading(): void {
    if (!this.isReading) {
      this.refresh();
    }
  }

  /**
   * Forgets the cached capabilities without reading again, for a sign-out: there is nobody to read
   * them for, and the next consumer that asks starts a fresh read as whoever is signed in then.
   */
  clear(): void {
    this.generation++;
    this.reading = undefined;
    this.isReading = false;
    this.cachedForAccountKey = null;
    this.forgetAnswer();
  }

  ngOnDestroy(): void {
    window.removeEventListener('focus', this.onWindowFocus);
    this.destroying$.next();
    this.destroying$.complete();
  }

  private ensureRead(): void {
    if (this.reading === undefined) {
      this.beginRead();
    }
  }

  private beginRead(): Observable<MemberAccessState> {
    const generation = ++this.generation;
    this.isReading = true;

    const reading = this.msalBroadcastService.inProgress$.pipe(
      filter((status: InteractionStatus) => status === InteractionStatus.None),
      take(1),
      switchMap(() => {
        const accountKey = activeAccountKey(this.authService);
        if (generation === this.generation) {
          this.cachedForAccountKey = accountKey;
        }
        // Nobody signed in is an answer, not a request: an unauthenticated read would only have
        // MsalInterceptor start an interactive sign-in nobody asked for.
        return accountKey === null ? of(UNAVAILABLE) : this.read();
      }),
      tap((answer) => this.publish(generation, answer)),
      shareReplay({ bufferSize: 1, refCount: false })
    );

    this.reading = reading;
    // Subscribed here, not by the caller: the cached answer has to be published whether or not
    // anybody is listening to this particular read.
    reading.pipe(takeUntil(this.destroying$)).subscribe();
    return reading;
  }

  private read(): Observable<MemberAccessState> {
    return this.http
      .get<MeAccessResponse>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/me/access`)
      .pipe(
        map(
          (response): MemberAccessState => ({
            status: 'granted',
            access: {
              role: response.role ?? null,
              capabilities: capabilitySet(response.capabilities ?? []),
              businessName: response.businessName,
              timeZoneId: response.timeZoneId,
              // Read as reported, never defaulted here: an absent field means "not known", and
              // what that means for the menu is the one rule on `OptionVisibility`.
              optionVisibility:
                response.historicalGstClassificationVisible === undefined
                  ? undefined
                  : {
                      historicalGstClassificationVisible: response.historicalGstClassificationVisible
                    }
            }
          })
        ),
        // A refusal or a failure is "not known", never an empty capability set: an empty set would
        // hide every entry in the application over one failed request. A refusal is also the
        // expected answer for somebody with no usable membership, so it is not reported to the
        // operator from here - which screen that person is shown is issue #529's account-state
        // routing.
        catchError(() => of(UNAVAILABLE))
      );
  }

  private publish(generation: number, answer: MemberAccessState): void {
    if (generation !== this.generation) {
      // A later invalidation owns the answer now; this one belongs to the previous identity or to
      // a capability set something has already decided to read again.
      return;
    }

    this.isReading = false;
    this.answeredAtMs = Date.now();
    this.state.next(answer);
  }

  private observeSignedInAccount(): void {
    signedInAccountMayHaveChanged(this.msalBroadcastService)
      .pipe(takeUntil(this.destroying$))
      .subscribe(() => this.applySignedInAccount());
  }

  private applySignedInAccount(): void {
    if (this.cachedForAccountKey === undefined) {
      // The cache belongs to no identity: nothing has been read yet, or the read in flight will
      // read it as whoever is signed in once MSAL's interaction settles. Either way there is
      // nothing to invalidate, which is also what coalesces the account event and the settling
      // interaction that both report one sign-in or account change.
      return;
    }

    const accountKey = activeAccountKey(this.authService);
    if (accountKey === this.cachedForAccountKey) {
      return;
    }

    if (accountKey === null) {
      this.clear();
      return;
    }

    // The previous identity's answer is dropped here, before the new read is issued or even
    // waited for (issue #603). It is not a weaker answer for the new identity - it is somebody
    // else's - and leaving it published, for as long as the new read takes, is long enough for a
    // guard to decide a navigation from it or for the shell to build a menu out of it.
    this.cachedForAccountKey = undefined;
    this.forgetAnswer();
    this.refresh();
  }

  /**
   * Publishes `unread` again, so nothing can be answered from what the previous identity read: a
   * consumer subscribing now waits for the next answer exactly as it would have before anything
   * had been read at all. The published answer is already `unread` when a read that has not
   * answered yet is the thing being invalidated.
   */
  private forgetAnswer(): void {
    this.answeredAtMs = null;
    if (this.state.value.status !== 'unread') {
      this.state.next(UNREAD);
    }
  }

  private refreshIfStale(): void {
    if (this.answeredAtMs === null || Date.now() - this.answeredAtMs < AccessService.staleAfterMs) {
      return;
    }

    this.refreshUnlessReading();
  }
}
