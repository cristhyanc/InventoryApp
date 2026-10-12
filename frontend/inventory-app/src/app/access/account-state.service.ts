import { HttpClient } from '@angular/common/http';
import { Injectable, OnDestroy } from '@angular/core';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { EventMessage, EventType, InteractionStatus } from '@azure/msal-browser';
import {
  BehaviorSubject,
  Observable,
  Subject,
  catchError,
  defer,
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
import { ConfigService } from '../services/config.service';

/**
 * The published `Inventory.Domain.Tenancy.AccountState` names, mirrored here rather than derived
 * (issue #523). A value outside this set is not expected from the API and is not a state this
 * build can route.
 */
export const ACCOUNT_STATES = [
  'Member',
  'NoMembership',
  'ApplicationPending',
  'ApplicationRejected',
  'MembershipRevoked',
  'BusinessDeactivated',
  'MembershipAmbiguous'
] as const;

export type AccountStateName = (typeof ACCOUNT_STATES)[number];

/** The `GET /api/me/account-state` response body (issue #523). */
interface MeAccountStateResponse {
  state: string;
  onboardingEnabled: boolean;
  rejectionReason: string | null;
}

/** Which state the signed-in person's account is in, as `GET /api/me/account-state` reports it. */
export interface AccountState {
  readonly state: AccountStateName;
  readonly onboardingEnabled: boolean;
  readonly rejectionReason: string | null;
}

/**
 * Whether the account state is known yet - the same three-way shape `MemberAccessState` uses on
 * `AccessService`, and for the same reason: `unread` is "nothing has asked yet", `unavailable` is
 * "asked and could not be answered", and routing a person away over the second would lock a
 * legitimate member out of their own page over a failed probe.
 */
export type AccountStateAnswer =
  | { readonly status: 'unread' }
  | { readonly status: 'known'; readonly accountState: AccountState }
  | { readonly status: 'unavailable' };

const UNREAD: AccountStateAnswer = { status: 'unread' };
const UNAVAILABLE: AccountStateAnswer = { status: 'unavailable' };

const KNOWN_STATES: ReadonlySet<string> = new Set<string>(ACCOUNT_STATES);

function toAccountStateName(value: string): AccountStateName {
  return KNOWN_STATES.has(value) ? (value as AccountStateName) : 'MembershipAmbiguous';
}

/** The MSAL events that mean the signed-in identity may have changed. */
const ACCOUNT_EVENTS: ReadonlySet<string> = new Set<string>([
  EventType.LOGIN_SUCCESS,
  EventType.LOGOUT_SUCCESS,
  EventType.ACTIVE_ACCOUNT_CHANGED
]);

/**
 * Which state the signed-in person's account is in, read once from `GET /api/me/account-state`
 * (issue #523) and cached for the account-state route guard and the shell's own business lookup
 * (issue #529).
 *
 * This is a sibling cache to `AccessService`, built the same way and invalidated on exactly the
 * same triggers (issue #328's cache-invalidation contract): sign-in, sign-out, an MSAL
 * active-account change, any `403` from a member endpoint other than `GET /api/me/account-state`
 * itself, window focus more than {@link staleAfterMs} after the last read, and an explicit
 * {@link refresh}. See `AccessService` for the rationale behind each trigger; it is not repeated
 * here.
 *
 * **A read that cannot be answered allows the application through.** `unavailable` is treated the
 * same way `AccessService` treats its own unreadable answer: the membership check the backend
 * applies to every business endpoint (`BusinessScopeMiddleware`) remains the real boundary, so
 * failing closed on a failed probe would lock a legitimate member out over a network blip, while
 * failing open costs nothing worse than the `403` that endpoint would have returned anyway.
 */
@Injectable({ providedIn: 'root' })
export class AccountStateService implements OnDestroy {
  /** Window focus this long after the last read re-reads the account state. */
  static readonly staleAfterMs = 5 * 60 * 1000;

  private readonly answer = new BehaviorSubject<AccountStateAnswer>(UNREAD);

  private reading?: Observable<AccountStateAnswer>;
  private isReading = false;
  private answeredAtMs: number | null = null;
  private readForAccountKey?: string | null;
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
   * The account state, now and whenever it changes. Subscribing reads it if nothing has read it
   * yet, exactly as `AccessService.access$` does.
   */
  readonly state$: Observable<AccountStateAnswer> = defer(() => {
    this.ensureRead();
    return this.answer;
  });

  /** Re-reads the account state, discarding whatever is cached, and emits the answer. */
  refresh(): Observable<AccountStateAnswer> {
    return this.beginRead();
  }

  /** Re-reads unless a read is already in flight, whose answer reports the server's current state anyway. */
  refreshUnlessReading(): void {
    if (!this.isReading) {
      this.refresh();
    }
  }

  /** Forgets the cached state without reading again, for a sign-out. */
  clear(): void {
    this.generation++;
    this.reading = undefined;
    this.isReading = false;
    this.answeredAtMs = null;
    this.readForAccountKey = null;
    this.answer.next(UNREAD);
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

  private beginRead(): Observable<AccountStateAnswer> {
    const generation = ++this.generation;
    this.isReading = true;

    const reading = this.msalBroadcastService.inProgress$.pipe(
      filter((status: InteractionStatus) => status === InteractionStatus.None),
      take(1),
      switchMap(() => {
        const accountKey = this.activeAccountKey();
        if (generation === this.generation) {
          this.readForAccountKey = accountKey;
        }
        return accountKey === null ? of(UNAVAILABLE) : this.read();
      }),
      tap((answer) => this.publish(generation, answer)),
      shareReplay({ bufferSize: 1, refCount: false })
    );

    this.reading = reading;
    reading.pipe(takeUntil(this.destroying$)).subscribe();
    return reading;
  }

  private read(): Observable<AccountStateAnswer> {
    return this.http
      .get<MeAccountStateResponse>(`${this.config.apiBaseUrl.replace(/\/$/, '')}/me/account-state`)
      .pipe(
        map(
          (response): AccountStateAnswer => ({
            status: 'known',
            accountState: {
              state: toAccountStateName(response.state),
              onboardingEnabled: response.onboardingEnabled,
              rejectionReason: response.rejectionReason
            }
          })
        ),
        catchError(() => of(UNAVAILABLE))
      );
  }

  private publish(generation: number, answer: AccountStateAnswer): void {
    if (generation !== this.generation) {
      return;
    }

    this.isReading = false;
    this.answeredAtMs = Date.now();
    this.answer.next(answer);
  }

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
    if (this.readForAccountKey === undefined) {
      return;
    }

    const accountKey = this.activeAccountKey();
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
    if (this.answeredAtMs === null || Date.now() - this.answeredAtMs < AccountStateService.staleAfterMs) {
      return;
    }

    this.refreshUnlessReading();
  }

  private activeAccountKey(): string | null {
    const account =
      this.authService.instance.getActiveAccount() ?? this.authService.instance.getAllAccounts()[0] ?? null;
    if (account === null) {
      return null;
    }

    return account.homeAccountId || account.localAccountId || account.username || '';
  }
}
