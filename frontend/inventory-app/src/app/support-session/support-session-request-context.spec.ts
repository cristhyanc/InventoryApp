import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { of } from 'rxjs';
import { AccessService, MemberAccessState } from '../access/access.service';
import { AccountStateService } from '../access/account-state.service';
import { FORBIDDEN_INTERCEPTORS } from '../access/forbidden-interceptors';
import { SignedInIdentityDoubles, signedInIdentity } from '../access/identity-cache.spec-helpers';
import { BusinessService } from '../services/business.service';
import { ConfigService } from '../services/config.service';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import { ToastService } from '../services/toast.service';
import { supportSessionMessageInterceptor } from './support-session-message.interceptor';
import { supportSessionInterceptor } from './support-session.interceptor';
import { SupportSessionService, SupportSessionState } from './support-session.service';

const API_BASE_URL = '/api';
const DASHBOARD = '/api/dashboard/summary';
const ACTIVE_PROBE = '/api/super-admin/support-sessions/active';
const END_CALL = '/api/super-admin/support-sessions/active/end';
const MEMBER_MESSAGE = "You don't have access to this.";

/** `GET .../active` as issue #550 answers it: the business and the instants, never the id. */
const ACTIVE_ON_SERVER = {
  businessId: 9,
  businessName: 'Snack Co',
  reason: 'Investigating a reported bug',
  startedAtUtc: '2026-02-01T00:00:00Z',
  expiresAtUtc: '2026-02-01T01:00:00Z'
};

/** What a caller of a business-scoped request was told, in the three ways it can be told it. */
interface Answer {
  next: jest.Mock;
  error: jest.Mock;
  complete: jest.Mock;
}

interface Harness {
  readonly session: SupportSessionService;
  readonly identity: SignedInIdentityDoubles;
  readonly toastError: jest.Mock;
  readonly accessRefreshUnlessReading: jest.Mock;
  readonly accountStateRefreshUnlessReading: jest.Mock;
  readonly businessReset: jest.Mock;
  readonly navigate: jest.Mock;
  /** Issues a business-scoped request and returns what its caller is eventually told. */
  issue(url?: string): { answer: Answer; request: TestRequest };
  /** Starts a session for `businessId`, answering the start call with `sessionId`. */
  start(businessId: number, sessionId: string, businessName?: string): void;
  /** Ends the session the way the banner does, from this tab. */
  end(): void;
  /** Starts the one-time boot probe and records what the shell is published. */
  observe(): SupportSessionState[];
  /** Forgets the business-context mutations recorded so far. */
  forgetContextChanges(): void;
}

/**
 * The production interceptor chain over the **real** `SupportSessionService`, with the API answered
 * by `HttpTestingController` so a test controls exactly when a response arrives (issue #669).
 *
 * The order mirrors `app.config.ts`: the three `403` interceptors, then the support-session message
 * interceptor, then `supportSessionInterceptor` innermost. `loadingInterceptor` is left out because
 * it only ends a spinner. The two member caches, the cached business, the router, the platform-admin
 * eligibility check and the toast are doubled, so what each of them was asked to do is observable;
 * nothing about the session's own context tracking is simulated.
 */
function configure(options: { granted?: boolean } = {}): Harness {
  const identity = signedInIdentity();
  const toastError = jest.fn();
  const accessRefreshUnlessReading = jest.fn();
  const accountStateRefreshUnlessReading = jest.fn();
  const accessRefresh = jest.fn(() => of({ status: 'unread' } as MemberAccessState));
  const businessReset = jest.fn();
  // The transition releases the page it withheld when the navigation settles (issue #670), so the
  // double answers a promise exactly as `Router.navigate` does.
  const navigate = jest.fn(() => Promise.resolve(true));

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(
        withInterceptors([...FORBIDDEN_INTERCEPTORS, supportSessionMessageInterceptor, supportSessionInterceptor])
      ),
      provideHttpClientTesting(),
      { provide: ConfigService, useValue: { apiBaseUrl: API_BASE_URL } },
      { provide: ToastService, useValue: { error: toastError } },
      { provide: Router, useValue: { url: '/dashboard', navigate } },
      { provide: BusinessService, useValue: { reset: businessReset } },
      {
        provide: AccessService,
        useValue: { refresh: accessRefresh, refreshUnlessReading: accessRefreshUnlessReading }
      },
      { provide: AccountStateService, useValue: { refreshUnlessReading: accountStateRefreshUnlessReading } },
      {
        provide: PlatformDiagnosticsAccessService,
        useValue: { isGranted: () => of(options.granted ?? false) }
      },
      { provide: MsalService, useValue: identity.msal },
      { provide: MsalBroadcastService, useValue: identity.broadcast }
    ]
  });

  const http = TestBed.inject(HttpClient);
  const httpMock = TestBed.inject(HttpTestingController);
  const session = TestBed.inject(SupportSessionService);

  return {
    session,
    identity,
    toastError,
    accessRefreshUnlessReading,
    accountStateRefreshUnlessReading,
    businessReset,
    navigate,
    issue: (url = DASHBOARD) => {
      const answer: Answer = { next: jest.fn(), error: jest.fn(), complete: jest.fn() };
      http.get(url).subscribe(answer);
      return { answer, request: httpMock.expectOne(url) };
    },
    start: (businessId, sessionId, businessName = 'Snack Co') => {
      session.start(businessId, 'Investigating a reported bug').subscribe();
      httpMock.expectOne(`/api/super-admin/support-sessions/${businessId}`).flush({
        sessionId,
        businessId,
        businessName,
        startedAtUtc: '2026-02-01T00:00:00Z',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      });
    },
    end: () => {
      session.end().subscribe();
      httpMock.expectOne(END_CALL).flush(null);
    },
    observe: () => {
      const observed: SupportSessionState[] = [];
      session.state$.subscribe((state) => observed.push(state));
      return observed;
    },
    forgetContextChanges: () => {
      businessReset.mockClear();
      navigate.mockClear();
      toastError.mockClear();
      accessRefreshUnlessReading.mockClear();
      accountStateRefreshUnlessReading.mockClear();
    }
  };
}

/** Nothing reached the caller, and so nothing reached the page it would have rendered. */
function expectDiscarded(answer: Answer): void {
  expect(answer.next).not.toHaveBeenCalled();
  expect(answer.error).not.toHaveBeenCalled();
  expect(answer.complete).toHaveBeenCalledTimes(1);
}

/**
 * A response from a superseded business context never reaches anything (issue #669).
 *
 * Issue #553 compared the session id a request presented against the current one, which left two
 * holes: a request sent with **no** id - every ordinary-context request, and every request of a tab
 * that cannot present the session - had no id to become stale against, so a delayed answer could
 * restore the administrator's own business data into a page now showing somebody else's; and the
 * `support_session_ended` reaction ran before that comparison, so one session's delayed refusal
 * ended whichever session was current by the time it arrived.
 *
 * Every request therefore captures a **context generation** as well, which the service invalidates
 * on every effective support-context change and on an identity change. The comparison happens
 * before anything reacts to the response, and this interceptor is innermost, so a superseded answer
 * reaches neither the session state, nor the page, nor the capability and account-state caches, nor
 * the global `403` notice.
 */
describe('superseded business contexts (issue #669)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
  });

  describe('a response that outlived the context it was sent in', () => {
    it('discards an ordinary-context answer that arrives after a support session started', () => {
      const harness = configure();
      const { answer, request } = harness.issue();

      harness.start(9, 'session-a');
      request.flush({ week: 1 });

      expectDiscarded(answer);
    });

    it('discards a support-view answer that arrives after the session ended', () => {
      const harness = configure();
      harness.start(9, 'session-a');
      const { answer, request } = harness.issue();

      harness.end();
      request.flush({ week: 1 });

      expectDiscarded(answer);
    });

    it('discards an answer from session A that arrives while session B is in view', () => {
      const harness = configure();
      harness.start(9, 'session-a');
      const { answer, request } = harness.issue();

      harness.end();
      harness.start(12, 'session-b', 'Other Co');
      request.flush({ week: 1 });

      expectDiscarded(answer);
    });

    it('discards an answer from a session that was ended and restarted for the same business', () => {
      const harness = configure();
      harness.start(9, 'session-a');
      const { answer, request } = harness.issue();

      harness.end();
      harness.start(9, 'session-b');
      request.flush({ week: 1 });

      expectDiscarded(answer);
    });

    /**
     * The id is `null` at both ends here, which is exactly what issue #553's comparison could not
     * see: the request began in ordinary context, a session for another business was looked at and
     * ended while it was in flight, and its answer is the administrator's own business data for a
     * page that has been through two context changes since.
     */
    it('discards an ordinary-context answer that outlived a whole session started and ended meanwhile', () => {
      const harness = configure();
      const { answer, request } = harness.issue();

      harness.start(9, 'session-a');
      harness.end();
      request.flush({ week: 1 });

      expect(harness.session.currentSessionId()).toBeNull();
      expectDiscarded(answer);
    });

    it('discards an answer that arrives after the signed-in identity changed', () => {
      const harness = configure();
      const { answer, request } = harness.issue();

      harness.identity.switchAccount('member-2');
      request.flush({ week: 1 });

      expectDiscarded(answer);
    });

    it('discards a support-view answer that arrives after the signed-in identity changed', () => {
      const harness = configure();
      harness.start(9, 'session-a');
      const { answer, request } = harness.issue();

      harness.identity.switchAccount('member-2');
      request.flush({ week: 1 });

      expectDiscarded(answer);
    });

    /** A tab that learns it cannot present the session renders no page (issue #668). */
    it('discards an answer that arrives after the boot probe reported an unresolved session', () => {
      const harness = configure({ granted: true });
      const { answer, request } = harness.issue();

      const observed = harness.observe();
      TestBed.inject(HttpTestingController).expectOne(ACTIVE_PROBE).flush(ACTIVE_ON_SERVER);
      request.flush({ week: 1 });

      expect(observed[observed.length - 1].kind).toBe('unresolved');
      expectDiscarded(answer);
    });

    it('leaves an answer alone when nothing about the context changed', () => {
      const harness = configure();
      harness.start(9, 'session-a');
      const { answer, request } = harness.issue();

      request.flush({ week: 1 });

      expect(answer.next).toHaveBeenCalledWith({ week: 1 });
      expect(answer.complete).toHaveBeenCalledTimes(1);
    });

    it('leaves an ordinary-context answer alone when nothing about the context changed', () => {
      const harness = configure();
      const { answer, request } = harness.issue();

      request.flush({ week: 1 });

      expect(answer.next).toHaveBeenCalledWith({ week: 1 });
    });
  });

  describe('a superseded failure', () => {
    it('reaches neither the global notice nor the member caches', () => {
      const harness = configure();
      const { answer, request } = harness.issue();

      harness.start(9, 'session-a');
      request.flush('refused', { status: 403, statusText: 'Forbidden' });

      expectDiscarded(answer);
      expect(harness.toastError).not.toHaveBeenCalled();
      expect(harness.accessRefreshUnlessReading).not.toHaveBeenCalled();
      expect(harness.accountStateRefreshUnlessReading).not.toHaveBeenCalled();
    });

    it('is discarded whatever its status', () => {
      const harness = configure();
      const { answer, request } = harness.issue();

      harness.start(9, 'session-a');
      request.flush('server error', { status: 500, statusText: 'Error' });

      expectDiscarded(answer);
    });

    it('still reports a current-context refusal, so nothing here hides a real one', () => {
      const harness = configure();
      const { answer, request } = harness.issue();

      request.flush('refused', { status: 403, statusText: 'Forbidden' });

      expect(answer.error).toHaveBeenCalledTimes(1);
      expect(harness.toastError).toHaveBeenCalledWith(MEMBER_MESSAGE);
      expect(harness.accessRefreshUnlessReading).toHaveBeenCalledTimes(1);
    });
  });

  describe('the "support_session_ended" refusal', () => {
    it('cannot end session B when it was sent under session A', () => {
      const harness = configure();
      harness.start(9, 'session-a');
      const { answer, request } = harness.issue();
      harness.end();
      harness.start(12, 'session-b', 'Other Co');
      const observed = harness.observe();
      harness.forgetContextChanges();

      request.flush({ code: 'support_session_ended' }, { status: 403, statusText: 'Forbidden' });

      expect(harness.session.currentSessionId()).toBe('session-b');
      expect(observed[observed.length - 1]).toEqual({
        kind: 'viewing',
        businessName: 'Other Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      });
      expect(harness.businessReset).not.toHaveBeenCalled();
      expect(harness.navigate).not.toHaveBeenCalled();
      expectDiscarded(answer);
    });

    it('cannot end the current session when it was sent before an identity change', () => {
      const harness = configure();
      harness.start(9, 'session-a');
      const { answer, request } = harness.issue();
      harness.forgetContextChanges();

      harness.identity.switchAccount('member-2');
      request.flush({ code: 'support_session_ended' }, { status: 403, statusText: 'Forbidden' });

      expect(harness.businessReset).not.toHaveBeenCalled();
      expect(harness.navigate).not.toHaveBeenCalled();
      expectDiscarded(answer);
    });

    /** The current session still dies on its own refusal, and the caller still sees nothing. */
    it('ends the session it was sent under, and fails closed', () => {
      const harness = configure();
      harness.start(9, 'session-a');
      const { answer, request } = harness.issue();
      const observed = harness.observe();
      harness.forgetContextChanges();

      request.flush({ code: 'support_session_ended' }, { status: 403, statusText: 'Forbidden' });

      expect(harness.session.currentSessionId()).toBeNull();
      expect(observed[observed.length - 1]).toEqual({ kind: 'inactive' });
      expect(harness.businessReset).toHaveBeenCalledTimes(1);
      expect(harness.navigate).toHaveBeenCalledWith(['/'], { onSameUrlNavigation: 'reload' });
      expect(harness.toastError).not.toHaveBeenCalled();
      expectDiscarded(answer);
    });

    it('leaves an ordinary-context request alone, because it presented no session at all', () => {
      const harness = configure();
      const { answer, request } = harness.issue();
      const observed = harness.observe();

      request.flush({ code: 'support_session_ended' }, { status: 403, statusText: 'Forbidden' });

      expect(observed[observed.length - 1]).toEqual({ kind: 'inactive' });
      expect(harness.businessReset).not.toHaveBeenCalled();
      expect(answer.error).toHaveBeenCalledTimes(1);
    });
  });
});
