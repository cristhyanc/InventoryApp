import { HttpClient } from '@angular/common/http';
import { Injectable, OnDestroy } from '@angular/core';
import { Router } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import {
  BehaviorSubject,
  Observable,
  Subject,
  Subscription,
  catchError,
  defer,
  distinctUntilChanged,
  map,
  of,
  switchMap,
  takeUntil,
  tap,
  timer
} from 'rxjs';
import { ConfigService } from '../services/config.service';
import { BusinessService } from '../services/business.service';
import { AccessService } from '../access/access.service';
import { activeAccountKey, signedInAccountMayHaveChanged } from '../access/signed-in-account';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import { ToastService } from '../services/toast.service';
import { SUPPORT_SESSION_ENDED_MESSAGE } from './support-session-messages';

/** The header a request presents to join a support session (`InventoryApi.Auth.SupportSessionHeader`). */
export const SUPPORT_SESSION_HEADER = 'X-Support-Session';

/**
 * The `BroadcastChannel` name the open tabs of one browser share a session's id and end over.
 *
 * It is exported so a test can act as one of those other tabs - which is the only way to exercise a
 * malformed or another identity's message (issue #670), since a legitimate sibling never sends one.
 */
export const SUPPORT_SESSION_BROADCAST_CHANNEL = 'inventory-app:support-session';

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
type SupportSessionBroadcastBody =
  | { readonly type: 'started'; readonly sessionId: string; readonly businessName: string; readonly expiresAtUtc: string }
  | { readonly type: 'ended' };

/**
 * The same message as it travels, which is with the signed-in identity it belongs to (issue #670).
 *
 * A `BroadcastChannel` is shared by every tab of the origin, and a support session belongs to one
 * administrator: a message naming anybody else is not this tab's session to adopt or to end. The
 * account key is the one MSAL already shares between the tabs of a browser
 * (`activeAccountKey`), never the session id, which no message but `started` carries at all.
 */
type SupportSessionBroadcast = SupportSessionBroadcastBody & { readonly accountKey: string };

/** A received message, before anything about it is known: every field has to be checked. */
type ReceivedBroadcast = Partial<{
  type: unknown;
  accountKey: unknown;
  sessionId: unknown;
  businessName: unknown;
  expiresAtUtc: unknown;
}>;

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
 * feature-detection `AppComponent` already uses for `matchMedia`. Every message names the identity
 * it belongs to and is checked field by field before it is acted on (issue #670,
 * {@link readBroadcast}): a malformed message, and one from another signed-in identity, can neither
 * adopt a session here nor end the one this tab is in.
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
 * **Every effective context change takes one transition path** (issue #670,
 * {@link applyBusinessContextChange}): a local start or end, the session's own expiry, a
 * `support_session_ended` refusal of the context that is still current, and a valid same-identity
 * `started`/`ended` notification from another tab all forget the cached business the shell reads for
 * every member (`BusinessService.reset`), report the change on {@link contextChanged$} so the shell
 * reads the business now in effect and publishes **its** time zone (issue #671), re-read the
 * capabilities (`AccessService.refresh`) - the support set inside a session, the administrator's own
 * (none) outside one - and take this tab to the dashboard of the business now in view, because
 * neither the page that was on screen before nor the one a stale capability set rendered belongs
 * to it.
 *
 * **Reaching the dashboard is not the same as replacing what is on it.** `router.navigate(['/'])`
 * alone leaves a tab that is *already* on `/` exactly as it was - the router has nothing to do, so
 * the page component is reused and the previous business's figures stay on screen under a banner
 * that has already changed or disappeared. The transition therefore publishes
 * {@link contextChanging$} around its navigation, which the shell withholds (and so destroys) the
 * routed page for, and asks for the destination to be re-validated with `onSameUrlNavigation:
 * 'reload'`; the page the operator then sees is a new instance that reads the effective context
 * from scratch.
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

  /**
   * Whether a business-context transition is still settling (issue #670). It is a count rather than
   * a flag because two transitions can overlap - a session ended and another started before the
   * first navigation settled - and the page may only come back when the last of them has.
   */
  private pendingContextChanges = 0;
  private readonly contextChangingSubject = new BehaviorSubject(false);

  /**
   * One emission per effective business-context change (issue #671), deliberately not derived from
   * {@link contextChangingSubject}: that one is a settled/unsettled flag, and two overlapping
   * transitions report `true` once between them, so the second business would never be read.
   */
  private readonly contextChangedSubject = new Subject<void>();

  /** The session's own lifetime running out, scheduled from the expiry every state carries. */
  private expiry?: Subscription;

  private booted = false;
  private readonly destroying$ = new Subject<void>();

  private readonly channel: BroadcastChannel | null =
    typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel(SUPPORT_SESSION_BROADCAST_CHANNEL);

  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService,
    private readonly business: BusinessService,
    private readonly access: AccessService,
    private readonly platformDiagnosticsAccess: PlatformDiagnosticsAccessService,
    private readonly router: Router,
    private readonly authService: MsalService,
    private readonly msalBroadcastService: MsalBroadcastService,
    private readonly toast: ToastService
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

  /**
   * Whether an effective business-context transition is still settling (issue #670): `true` from
   * the moment one starts until the dashboard navigation it performs has settled.
   *
   * `AppComponent` withholds the routed page while it is `true`, and that is what *clears* the
   * previous business's data rather than merely navigating away from it: withholding the page
   * destroys the component holding those figures, and releasing it again creates a new one that
   * reads the effective context from scratch. It is the same gate the shell already applies while
   * the business lookup settles (issue #499) and while a tab cannot present the session
   * (issue #668), and it works on `/` - where the navigation itself has nothing to do - exactly as
   * it works anywhere else.
   */
  readonly contextChanging$: Observable<boolean> = this.contextChangingSubject
    .asObservable()
    .pipe(distinctUntilChanged());

  /**
   * That the effective business has changed, once per transition (issue #671), published the moment
   * the previous context's cached business is dropped and before anything in the new context is
   * issued.
   *
   * `AppComponent` reads the effective business again from here. {@link contextChanging$} cannot say
   * this: it reports whether a transition is *settling*, so a session ended and another started
   * before the first navigation finished emits one `true` for the two of them and the second
   * business's own time zone would never be read - while `BusinessService.reset()` had already
   * dropped the first one's.
   */
  readonly contextChanged$: Observable<void> = this.contextChangedSubject.asObservable();

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
    if (!this.knowsOfSession()) {
      return;
    }
    // The administrator did not ask for this, so they are told why the view went away (issue #681).
    this.applyEnded(true, true);
  }

  /**
   * Ends the caller's session before they sign out (issue #681), so signing out does not leave it
   * running on the server for the rest of its lifetime, with other open tabs still presenting it.
   *
   * It always completes, with nothing, and never errors: a sign-out must happen whether or not the
   * end could be confirmed. A refused or failed end still forgets the session in this tab - the id
   * must not outlive the identity it was granted to - but only a confirmed end is broadcast, because
   * a sibling tab dropping a session the server still holds would be telling it something false.
   * Nothing is navigated: the page is about to be replaced by the sign-out redirect anyway.
   */
  endBeforeSignOut(): Observable<void> {
    if (!this.knowsOfSession()) {
      return of(undefined);
    }

    return this.http.post(`${this.baseUrl}/active/end`, {}).pipe(
      map(() => true),
      catchError(() => of(false)),
      tap((ended) => {
        this.forgetSession();
        if (ended) {
          this.broadcast({ type: 'ended' });
        }
      }),
      map(() => undefined)
    );
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

  /**
   * Ends the local view. `notify` is for an end the administrator did not ask for themselves - the
   * session's own expiry, or the server refusing a call because it is over - which used to take them
   * back to their own dashboard with no word of why (issue #681). An end they pressed, or one they
   * pressed in another tab, needs no notice.
   */
  private applyEnded(broadcastIt: boolean, notify = false): void {
    this.forgetSession();
    if (broadcastIt) {
      this.broadcast({ type: 'ended' });
    }
    if (notify) {
      this.toast.warning(SUPPORT_SESSION_ENDED_MESSAGE, 'Support session ended');
    }
    this.applyBusinessContextChange();
  }

  /** Whether this tab holds a session's id or has been told one is running. */
  private knowsOfSession(): boolean {
    return this.sessionId !== null || this.stateSubject.value.kind !== 'inactive';
  }

  /** Drops the id and the banner state, and supersedes every request still in flight. */
  private forgetSession(): void {
    this.sessionId = null;
    this.invalidateRequestContext();
    this.publish(INACTIVE);
  }

  /**
   * The one path every effective business-context change takes (issue #670): a local start or end,
   * the session's own expiry, a `support_session_ended` refusal of the current context, and a valid
   * same-identity `started`/`ended` notification from another tab.
   *
   * It drops the cached business and re-reads the capabilities, then takes this tab to the
   * dashboard of the business now in view - and makes sure the page the operator ends up looking at
   * is a **new** one. {@link contextChanging$} is what does that: the shell withholds the routed
   * page while it is `true`, which destroys the component the previous business's figures were
   * rendered by, and creates a fresh one when it goes `false` again. That is the whole reason it
   * exists - `router.navigate(['/'])` has nothing to do for a tab already on `/`, and
   * `onSameUrlNavigation: 'reload'` only re-runs the guards and resolvers of a route whose
   * component the router still reuses.
   */
  private applyBusinessContextChange(): void {
    this.business.reset();
    // Dropping the cached business is not reading the new one (issue #671): the business this tab
    // resolves after this transition - the session's inside a support view, the administrator's own
    // outside one - has its own time zone, and every instant the next page renders is rendered in
    // it. The shell is what reads it, because the shell is what holds the page back until it has
    // answered. Published here, before the capability re-read and the navigation below, so that
    // read belongs to the new context rather than being discarded as an answer from the old one.
    this.contextChangedSubject.next();
    this.access.refresh().subscribe();

    this.pendingContextChanges++;
    this.contextChangingSubject.next(true);
    const settled = (): void => {
      this.pendingContextChanges--;
      if (this.pendingContextChanges === 0) {
        this.contextChangingSubject.next(false);
      }
    };
    // A navigation that is refused, redirected or superseded releases the page just the same: the
    // alternative is a shell that withholds every page for the rest of the browser session.
    this.router.navigate(['/'], { onSameUrlNavigation: 'reload' }).then(settled, settled);
  }

  /**
   * Ends the local view when the session's own lifetime runs out (issue #670), through the same
   * transition everything else takes. It is scheduled from the expiry every active state carries,
   * so the tab a session was started in, a tab it was shared with and a tab that only learned about
   * it from `GET .../active` all end it at the same instant without telling each other - which is
   * why an expiry is not broadcast.
   *
   * **The browser's clock is not the authority here, and is never allowed to act like it.** An
   * unreadable instant, or one this clock says has already passed, schedules nothing at all: the
   * server decides when a session is over and refuses the next call with `support_session_ended`
   * (which reaches the same transition), and a clock that is hours fast must not be able to end a
   * session that has just been granted.
   */
  private scheduleExpiry(expiresAtUtc: string | null): void {
    this.expiry?.unsubscribe();
    this.expiry = undefined;
    if (expiresAtUtc === null) {
      return;
    }

    const remainingMs = Date.parse(expiresAtUtc) - Date.now();
    if (!Number.isFinite(remainingMs) || remainingMs <= 0) {
      return;
    }

    this.expiry = timer(remainingMs)
      .pipe(takeUntil(this.destroying$))
      .subscribe(() => this.applyEnded(false, true));
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
    if (!hadIdentity) {
      return;
    }

    if (this.knowsOfSession()) {
      // A session belongs to the administrator it was granted to (issue #681). The identity signed
      // in now cannot end it - the end call is answered for the caller's own session only - so this
      // tab forgets it instead: its id must not be attached to somebody else's requests, and its
      // banner must not hide their write controls. Nothing is broadcast, because a message goes out
      // under the identity signed in now, which no tab still holding that session would accept. The
      // shell discards the previous identity's business context itself (issue #604).
      this.forgetSession();
      return;
    }
    this.invalidateRequestContext();
  }

  /**
   * Publishes the state, and schedules (or cancels) the expiry it carries - the one place that
   * happens, so an active state and the instant it ends at can never be published apart.
   */
  private publish(state: SupportSessionState): void {
    this.scheduleExpiry(state.kind === 'inactive' ? null : state.expiresAtUtc);
    this.stateSubject.next(state);
  }

  private broadcast(message: SupportSessionBroadcastBody): void {
    if (this.contextAccountKey === null) {
      // Nobody is signed in here, so there is no identity to send this under and no sibling tab
      // could act on it: a session is always started and ended by an authenticated administrator.
      return;
    }
    const outgoing: SupportSessionBroadcast = { ...message, accountKey: this.contextAccountKey };
    this.channel?.postMessage(outgoing);
  }

  /**
   * What a received `BroadcastChannel` message may be acted on as, or `null` when it may not be
   * (issue #670).
   *
   * The channel is shared by every tab of this origin and carries a session **id** - the one
   * credential this application holds - so nothing about a message is assumed. It must name the
   * identity signed in here (which also refuses one from a tab signed in as somebody else, and one
   * arriving at a tab that does not know who is signed in yet), it must be one of the two message
   * kinds by exact name, and a `started` must carry a non-empty id, a business name and an expiry.
   * Anything else is ignored entirely: it can neither adopt a session here - the `started` handler
   * is the only thing in this service that ever learns an id it was not answered directly - nor end
   * the one this tab is in.
   */
  private readBroadcast(data: unknown): SupportSessionBroadcastBody | null {
    if (typeof data !== 'object' || data === null) {
      return null;
    }

    const message = data as ReceivedBroadcast;
    if (typeof message.accountKey !== 'string' || message.accountKey !== this.contextAccountKey) {
      return null;
    }

    if (message.type === 'ended') {
      return { type: 'ended' };
    }

    if (message.type !== 'started') {
      return null;
    }

    const { sessionId, businessName, expiresAtUtc } = message;
    if (
      typeof sessionId !== 'string' ||
      sessionId === '' ||
      typeof businessName !== 'string' ||
      typeof expiresAtUtc !== 'string'
    ) {
      return null;
    }

    return { type: 'started', sessionId, businessName, expiresAtUtc };
  }

  private readonly onBroadcastMessage = (event: MessageEvent<unknown>): void => {
    const message = this.readBroadcast(event.data);
    if (message === null) {
      return;
    }

    if (message.type === 'started') {
      this.sessionId = message.sessionId;
      this.invalidateRequestContext();
      this.publish({ kind: 'viewing', businessName: message.businessName, expiresAtUtc: message.expiresAtUtc });
      // The same transition a local start takes (issue #670): this tab is now reading another
      // business, so the cached business, the capabilities and the page on screen all belong to a
      // context it has left - issue #553 moved only the id and the banner here.
      this.applyBusinessContextChange();
      return;
    }

    if (this.knowsOfSession()) {
      this.applyEnded(false);
    }
  };

  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/super-admin/support-sessions`;
  }
}
