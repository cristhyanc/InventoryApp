import { HttpClient } from '@angular/common/http';
import { Injectable, OnDestroy } from '@angular/core';
import { Router } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import {
  BehaviorSubject,
  Observable,
  Subject,
  catchError,
  defer,
  distinctUntilChanged,
  map,
  of,
  switchMap,
  takeUntil,
  tap
} from 'rxjs';
import { ConfigService } from '../services/config.service';
import { BusinessService } from '../services/business.service';
import { AccessService } from '../access/access.service';
import { activeAccountKey, signedInAccountMayHaveChanged } from '../access/signed-in-account';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';

/** The header a request presents to join a support session (`InventoryApi.Auth.SupportSessionHeader`). */
export const SUPPORT_SESSION_HEADER = 'X-Support-Session';

/** The `BroadcastChannel` name the open tabs of one browser share a session's id and end over. */
const BROADCAST_CHANNEL_NAME = 'inventory-app:support-session';

/** What a session names: never its id, which this application never reads back either. */
export interface SupportSessionDetails {
  readonly businessName: string;
  readonly expiresAtUtc: string;
}

/**
 * What this tab can do about the administrator's support session (issues #553 and #668).
 *
 * - `inactive` - nothing is running as far as this tab knows, so the tab is in ordinary context:
 *   its requests carry no support header and resolve the administrator's own membership.
 * - `viewing` - an **effective** local support view. This tab holds the session id, so its requests
 *   present `X-Support-Session`, the API resolves the session's business, and the "Viewing as"
 *   banner is the truth about what is on screen.
 * - `unresolved` - a session is active on the server for this administrator, but this tab does not
 *   hold its id and never can (it is memory-only, and the active read deliberately never answers
 *   it). Its requests would resolve the administrator's **own** membership instead, so this tab
 *   must not label anything "Viewing as" and must not render or fetch business-scoped page data at
 *   all; `SupportSessionBlockedComponent` explains that and offers **End session**.
 */
export type SupportSessionState =
  | { readonly kind: 'inactive' }
  | ({ readonly kind: 'viewing' } & SupportSessionDetails)
  | ({ readonly kind: 'unresolved' } & SupportSessionDetails);

const INACTIVE: SupportSessionState = { kind: 'inactive' };

/**
 * The business context one request was sent in, captured by `supportSessionInterceptor` before the
 * request leaves and compared against the current one before its answer is allowed to change
 * anything (issue #669).
 *
 * The `sessionId` is what the request actually presented, and `null` for an ordinary-context
 * request. The `generation` is everything the id cannot see: a session started, ended or replaced
 * while the request was in flight, a tab learning it cannot present the session at all, and a
 * change of signed-in identity. An ordinary-context request has no id to become stale against -
 * which is exactly how a delayed answer used to restore the administrator's own business data into
 * a page that had moved on - so the generation is what makes it comparable.
 */
export interface SupportRequestContext {
  readonly sessionId: string | null;
  readonly generation: number;
}

interface StartedSupportSession {
  readonly sessionId: string;
  readonly businessId: number;
  readonly businessName: string;
  readonly startedAtUtc: string;
  readonly expiresAtUtc: string;
}

interface ActiveSupportSession {
  readonly businessId: number;
  readonly businessName: string | null;
  readonly reason: string;
  readonly startedAtUtc: string;
  readonly expiresAtUtc: string;
}

/** The two things one tab tells its siblings over `BroadcastChannel` - never the storage or the URL. */
type SupportSessionBroadcast =
  | { readonly type: 'started'; readonly sessionId: string; readonly businessName: string; readonly expiresAtUtc: string }
  | { readonly type: 'ended' };

/**
 * Starting, ending and sharing the Super Admin's one support session across every page and every
 * open tab (issue #553), over the three control endpoints issue #550 built
 * (`/api/super-admin/support-sessions/*`).
 *
 * **The session id lives in this instance's memory only.** It is never written to `localStorage`,
 * `sessionStorage` or the URL - {@link currentSessionId} is read by `supportSessionInterceptor`
 * alone, to set the `X-Support-Session` header while a session is active and to tell a stale
 * response from a current one. A reload therefore forgets it, which is why {@link ensureBoot} below
 * exists.
 *
 * **Tabs share the id through `BroadcastChannel`, not through anything persisted.** Starting a
 * session broadcasts it to every other open tab of this origin, so each of them can send the header
 * too; ending one - from this tab, from another tab, or because the server answered
 * `support_session_ended` on some unrelated call - broadcasts (or locally applies) the same "nothing
 * is active now" state everywhere. `BroadcastChannel` is unavailable in this build's Jest
 * environment, so it is used only when the global exists (`typeof BroadcastChannel`), the same
 * feature-detection `AppComponent` already uses for `matchMedia`.
 *
 * **A tab that was never told the id learns a session is running, and fails closed** (issue #668).
 * A freshly opened or reloaded tab cannot have received a `BroadcastChannel` message for a session
 * that started before it existed, so it asks `GET .../active` once instead - the one read that is
 * resolved from the authenticated actor rather than from a presented id (issue #550's own design:
 * the id is returned by the start call and nowhere else). Such a tab publishes
 * `{ kind: 'unresolved' }` and **not** `viewing`: without the id its requests would carry no
 * support header and resolve the administrator's own membership, so labelling the page "Viewing as
 * &lt;business&gt;" would label the administrator's own data as somebody else's. The tab explains
 * itself and offers **End session** instead ({@link SupportSessionState}), and recovers only the two
 * honest ways - another tab's `started` broadcast arriving with the id, or the session being ended
 * and a new one started through the explicit start flow. Nothing here reads the id out of the probe,
 * storage or the URL, and nothing restarts the session on the administrator's behalf.
 *
 * **Start, end and expiry all do the same three things**: forget the cached business the shell
 * reads for every member (`BusinessService.reset`), re-read the capabilities
 * (`AccessService.refresh`) - the support set inside a session, the administrator's own (none)
 * outside one - and navigate to the dashboard, because neither the page that was on screen before
 * nor the one a stale capability set rendered belongs to the business now in view.
 *
 * **Every one of those changes also invalidates the request context** (issue #669). This service is
 * the one place that knows which business a request would resolve, so it is also the one place that
 * can say whether an answer still belongs to the context that asked for it:
 * {@link captureRequestContext} and {@link isCurrentContext} are that question, asked by
 * `supportSessionInterceptor` and nothing else. The generation moves on a session starting, ending
 * or being replaced (here, in another tab, or because the server refused a request with
 * `support_session_ended`), on a tab learning from the boot probe that it cannot present the
 * session at all, and on the signed-in identity changing - because each of those changes which
 * business the next request resolves, or whose data an answer already in flight belongs to.
 */
@Injectable({ providedIn: 'root' })
export class SupportSessionService implements OnDestroy {
  private readonly stateSubject = new BehaviorSubject<SupportSessionState>(INACTIVE);

  /** The session id, held in memory only. Read by `supportSessionInterceptor` and nothing else. */
  private sessionId: string | null = null;

  /**
   * Bumped by every effective business-context change (issue #669). A request carrying an older
   * generation was sent in a context that no longer exists, whether or not it presented an id.
   */
  private contextGeneration = 0;

  /**
   * The signed-in identity the current generation belongs to, and `null` while nobody is signed in.
   *
   * It starts as whoever MSAL already reports, and a later notification that resolves to the same
   * key changes nothing - one sign-in reports itself twice (an account event and `inProgress$`
   * settling), and neither may invalidate a context. Learning an identity where there was none is
   * not a change either: a request issued before anybody was signed in waits for MSAL's token
   * before it is even sent, so invalidating there would discard the first answer of every page
   * loaded straight into a sign-in redirect. Losing one (a sign-out) and swapping one for another
   * both are.
   */
  private contextAccountKey: string | null;

  private booted = false;
  private readonly destroying$ = new Subject<void>();

  private readonly channel: BroadcastChannel | null =
    typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel(BROADCAST_CHANNEL_NAME);

  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService,
    private readonly business: BusinessService,
    private readonly access: AccessService,
    private readonly platformDiagnosticsAccess: PlatformDiagnosticsAccessService,
    private readonly router: Router,
    private readonly authService: MsalService,
    private readonly msalBroadcastService: MsalBroadcastService
  ) {
    this.channel?.addEventListener('message', this.onBroadcastMessage);
    this.contextAccountKey = activeAccountKey(this.authService);
    this.observeSignedInAccount();
  }

  /** The shell's own support-session state; subscribing starts the one-time boot check above. */
  readonly state$: Observable<SupportSessionState> = defer(() => {
    this.ensureBoot();
    return this.stateSubject;
  });

  /** The id to send as `X-Support-Session`, or `null` while no session is active in this tab. */
  currentSessionId(): string | null {
    return this.sessionId;
  }

  /**
   * The business context a request is about to be sent in (issue #669): the id it will present, and
   * the generation that id alone cannot tell apart. Captured by `supportSessionInterceptor` for
   * every business-scoped request, including one sent with no id at all.
   */
  captureRequestContext(): SupportRequestContext {
    return { sessionId: this.currentSessionId(), generation: this.contextGeneration };
  }

  /**
   * Whether `context` is still the context this tab is in - asked of an answer before it is allowed
   * to change the session, the page or anything the interceptors above would report.
   *
   * Both halves are compared: the generation covers every change this service made, and the id is
   * the one thing about a request that the server itself saw, so a session replaced between a
   * capture and its answer is caught even if a generation were ever missed.
   */
  isCurrentContext(context: SupportRequestContext): boolean {
    return context.generation === this.contextGeneration && context.sessionId === this.currentSessionId();
  }

  /**
   * Whether a support session is active now, for every page's write controls (issue #554): a
   * session grants several capabilities a write button is otherwise shown for (`InventoryOperate`,
   * `PurchasingOperate`, `BusinessManage` and others - see
   * `Inventory.Application.Access.SupportSessionAccessPolicy`), so hiding or disabling a write
   * control is this flag's job and never the capability check's. Subscribing starts the same
   * one-time boot check {@link state$} does.
   *
   * An `unresolved` session counts as active here, which is the fail-closed answer: the shell does
   * not render a business page in that state at all (issue #668), and a control that somehow
   * rendered must not offer a write either.
   */
  readonly active$: Observable<boolean> = this.state$.pipe(
    map((state) => state.kind !== 'inactive'),
    distinctUntilChanged()
  );

  /** Starts a session for `businessId`, with a required reason. */
  start(businessId: number, reason: string): Observable<StartedSupportSession> {
    return this.http.post<StartedSupportSession>(`${this.baseUrl}/${businessId}`, { reason }).pipe(
      tap((started) => {
        this.sessionId = started.sessionId;
        // Before the capability re-read below is issued, so that read belongs to the new context
        // rather than being discarded as an answer from the one this call just replaced.
        this.invalidateRequestContext();
        this.publish({ kind: 'viewing', businessName: started.businessName, expiresAtUtc: started.expiresAtUtc });
        this.broadcast({
          type: 'started',
          sessionId: started.sessionId,
          businessName: started.businessName,
          expiresAtUtc: started.expiresAtUtc
        });
        this.applyBusinessContextChange();
      })
    );
  }

  /** Ends the caller's own session, whichever business it was reading - this tab's id or not. */
  end(): Observable<void> {
    return this.http.post(`${this.baseUrl}/active/end`, {}).pipe(
      map(() => undefined),
      tap(() => this.applyEnded(true))
    );
  }

  /**
   * The "any call answered `support_session_ended`" signal (issue #553's acceptance criteria),
   * read by `supportSessionInterceptor` alone. Idempotent: a second call, or one for a tab that was
   * never active, does nothing further.
   */
  handleSessionEndedSignal(): void {
    if (this.sessionId === null && this.stateSubject.value.kind === 'inactive') {
      return;
    }
    this.applyEnded(true);
  }

  ngOnDestroy(): void {
    this.channel?.removeEventListener('message', this.onBroadcastMessage);
    this.channel?.close();
    this.destroying$.next();
    this.destroying$.complete();
  }

  private ensureBoot(): void {
    if (this.booted) {
      return;
    }
    this.booted = true;

    this.platformDiagnosticsAccess
      .isGranted()
      .pipe(
        switchMap((granted) => (granted ? this.readActive() : of(null))),
        takeUntil(this.destroying$)
      )
      .subscribe((active) => {
        if (active !== null && this.sessionId === null && this.stateSubject.value.kind === 'inactive') {
          // Unresolved, never `viewing` (issue #668): the read says a session is running, and this
          // tab has no id to present on its requests, so the business it names is not what this tab
          // would be shown. Nothing here asks for the id, and nothing starts a replacement session.
          //
          // The context moves on even though the id does not (issue #669): this tab renders no
          // business page from here, so an answer to a request it issued while it still believed
          // itself to be in ordinary context must not reach one either.
          this.invalidateRequestContext();
          this.publish({
            kind: 'unresolved',
            businessName: active.businessName ?? '',
            expiresAtUtc: active.expiresAtUtc
          });
        }
      });
  }

  private readActive(): Observable<ActiveSupportSession | null> {
    return this.http
      .get<ActiveSupportSession>(`${this.baseUrl}/active`)
      .pipe(catchError(() => of(null)));
  }

  private applyEnded(broadcastIt: boolean): void {
    this.sessionId = null;
    this.invalidateRequestContext();
    this.publish(INACTIVE);
    if (broadcastIt) {
      this.broadcast({ type: 'ended' });
    }
    this.applyBusinessContextChange();
  }

  private applyBusinessContextChange(): void {
    this.business.reset();
    this.access.refresh().subscribe();
    this.router.navigate(['/']);
  }

  /**
   * Moves the request context on (issue #669), so every answer still in flight is superseded.
   *
   * It is called before the new context issues anything of its own, and it is deliberately a
   * counter rather than a comparison of states: two contexts that look alike - ordinary context
   * before and after a whole session was looked at and ended, the same business viewed under a new
   * session - are still two different contexts, and an answer from the first belongs to neither the
   * page nor the session the second is in.
   */
  private invalidateRequestContext(): void {
    this.contextGeneration++;
  }

  private observeSignedInAccount(): void {
    signedInAccountMayHaveChanged(this.msalBroadcastService)
      .pipe(takeUntil(this.destroying$))
      .subscribe(() => this.applySignedInAccount());
  }

  /**
   * An actual change of signed-in identity supersedes every answer in flight: it was asked for as
   * somebody else, whether or not a support session was involved, and the capabilities, the cached
   * business and the account state all belong to whoever is signed in now. See
   * {@link contextAccountKey} for which notifications count as a change.
   */
  private applySignedInAccount(): void {
    const accountKey = activeAccountKey(this.authService);
    if (accountKey === this.contextAccountKey) {
      return;
    }

    const hadIdentity = this.contextAccountKey !== null;
    this.contextAccountKey = accountKey;
    if (hadIdentity) {
      this.invalidateRequestContext();
    }
  }

  private publish(state: SupportSessionState): void {
    this.stateSubject.next(state);
  }

  private broadcast(message: SupportSessionBroadcast): void {
    this.channel?.postMessage(message);
  }

  private readonly onBroadcastMessage = (event: MessageEvent<SupportSessionBroadcast>): void => {
    const message = event.data;
    if (message.type === 'started') {
      this.sessionId = message.sessionId;
      this.invalidateRequestContext();
      this.publish({ kind: 'viewing', businessName: message.businessName, expiresAtUtc: message.expiresAtUtc });
      return;
    }

    if (this.sessionId !== null || this.stateSubject.value.kind !== 'inactive') {
      this.applyEnded(false);
    }
  };

  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/super-admin/support-sessions`;
  }
}
