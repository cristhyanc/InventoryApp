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
  switchMap,
  take,
  takeUntil,
  tap
} from 'rxjs';
import { ConfigService } from '../services/config.service';
import { Capability, CapabilitySet, capabilitySet } from './capabilities';
import { activeAccountKey, signedInAccountMayHaveChanged } from './signed-in-account';

/** The `GET /api/me/access` response body (issue #521). */
interface MeAccessResponse {
  role: string;
  capabilities: string[];
  businessName: string;
  timeZoneId: string;
}

/** What the signed-in member may do, as `GET /api/me/access` reported it. */
export interface MemberAccess {
  /** The member's own role name - `Owner`, `Manager` or `Operator` - for display only. */
  readonly role: string;
  readonly capabilities: CapabilitySet;
  readonly businessName: string;
  readonly timeZoneId: string;
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
   * The signed-in identity the cache belongs to: `undefined` before anything has been read, and
   * `null` once it has been read with nobody signed in.
   */
  private readForAccountKey?: string | null;

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
   */
  allowsAny(capabilities: readonly Capability[]): Observable<boolean> {
    return this.access$.pipe(
      filter((state) => state.status !== 'unread'),
      map((state) =>
        state.status === 'granted'
          ? capabilities.some((capability) => state.access.capabilities.has(capability))
          : true
      ),
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
    this.answeredAtMs = null;
    this.readForAccountKey = null;
    this.state.next(UNREAD);
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
          this.readForAccountKey = accountKey;
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
              role: response.role,
              capabilities: capabilitySet(response.capabilities ?? []),
              businessName: response.businessName,
              timeZoneId: response.timeZoneId
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
    if (this.readForAccountKey === undefined) {
      // Nothing has been read yet, so the first read will read it as whoever is signed in then.
      return;
    }

    const accountKey = activeAccountKey(this.authService);
    if (accountKey === this.readForAccountKey) {
      return;
    }

    if (accountKey === null) {
      this.clear();
      return;
    }

    this.refresh();
  }

  private refreshIfStale(): void {
    if (this.answeredAtMs === null || Date.now() - this.answeredAtMs < AccessService.staleAfterMs) {
      return;
    }

    this.refreshUnlessReading();
  }
}
