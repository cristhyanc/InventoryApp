import { HttpClient } from '@angular/common/http';
import { Injectable, OnDestroy } from '@angular/core';
import { Router } from '@angular/router';
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
 */
@Injectable({ providedIn: 'root' })
export class SupportSessionService implements OnDestroy {
  private readonly stateSubject = new BehaviorSubject<SupportSessionState>(INACTIVE);

  /** The session id, held in memory only. Read by `supportSessionInterceptor` and nothing else. */
  private sessionId: string | null = null;

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
    private readonly router: Router
  ) {
    this.channel?.addEventListener('message', this.onBroadcastMessage);
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
