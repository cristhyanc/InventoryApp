import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { EventType, InteractionStatus } from '@azure/msal-browser';
import { Subject, firstValueFrom, of, throwError } from 'rxjs';
import { AccessService, MemberAccessState } from '../access/access.service';
import { SignedInIdentityDoubles, signedInIdentity } from '../access/identity-cache.spec-helpers';
import { ConfigService } from '../services/config.service';
import { BusinessService } from '../services/business.service';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import {
  FakeBroadcastChannel,
  installBroadcastChannel,
  removeBroadcastChannel
} from './broadcast-channel.spec-helpers';
import {
  SUPPORT_SESSION_BROADCAST_CHANNEL,
  SupportSessionService,
  SupportSessionState
} from './support-session.service';

const STARTED_RESPONSE = {
  sessionId: 'session-abc',
  businessId: 9,
  businessName: 'Snack Co',
  startedAtUtc: '2026-02-01T00:00:00Z',
  expiresAtUtc: '2026-02-01T01:00:00Z'
};

/** `GET .../active` as issue #550 answers it: the business and the instants, never the id. */
const ACTIVE_ON_SERVER = {
  businessId: 9,
  businessName: 'Snack Co',
  reason: 'Investigating a reported bug',
  startedAtUtc: '2026-02-01T00:00:00Z',
  expiresAtUtc: '2026-02-01T01:00:00Z'
};

/**
 * The dashboard navigation every transition performs (issue #670). The destination is re-validated
 * even when the tab is already on it; what replaces the page itself is the shell withholding it,
 * which `app.component.spec.ts` and `support-session-context-transition.spec.ts` cover.
 */
const DASHBOARD_NAVIGATION = { onSameUrlNavigation: 'reload' };

/** The identity `signedInIdentity()` reports as signed in, as a broadcast message names it. */
const ACCOUNT_KEY = 'member-1';

interface Harness {
  service: SupportSessionService;
  post: jest.Mock;
  get: jest.Mock;
  businessReset: jest.Mock;
  accessRefresh: jest.Mock;
  navigate: jest.Mock;
  isGranted: jest.Mock;
  identity: SignedInIdentityDoubles;
  states(): SupportSessionState[];
}

/**
 * The service is constructed directly rather than through `TestBed`, the same way `AccessService`'s
 * own spec does it (`access.service.spec.ts`): its dependencies are plain constructor parameters, so
 * a hand-written double of each keeps these tests about the session's own rules.
 */
function harness(
  options: {
    granted?: boolean;
    activeOnServer?: unknown;
    post?: jest.Mock;
    get?: jest.Mock;
    navigate?: jest.Mock;
    signedIn?: string | null;
  } = {}
): Harness {
  const post = options.post ?? jest.fn(() => of(STARTED_RESPONSE));
  const get = options.get ?? jest.fn(() => of(options.activeOnServer ?? null));
  const businessReset = jest.fn();
  const accessRefresh = jest.fn(() => of({ status: 'unread' } as MemberAccessState));
  // The real `Router.navigate` answers a promise, and the transition releases the withheld page
  // when it settles (issue #670), so the double has to answer one too.
  const navigate = options.navigate ?? jest.fn(() => Promise.resolve(true));
  const isGranted = jest.fn(() => of(options.granted ?? true));
  const identity = signedInIdentity({ signedIn: options.signedIn });

  const service = new SupportSessionService(
    { post, get } as unknown as HttpClient,
    { apiBaseUrl: '/api' } as unknown as ConfigService,
    { reset: businessReset } as unknown as BusinessService,
    { refresh: accessRefresh } as unknown as AccessService,
    { isGranted } as unknown as PlatformDiagnosticsAccessService,
    { navigate } as unknown as Router,
    identity.msal,
    identity.broadcast
  );

  return {
    service,
    post,
    get,
    businessReset,
    accessRefresh,
    navigate,
    isGranted,
    identity,
    states: () => {
      const observed: SupportSessionState[] = [];
      service.state$.subscribe((state) => observed.push(state));
      return observed;
    }
  };
}

/**
 * Another open tab of this origin, which can reach the service only the way a real one does. It is
 * what lets a test post a message no legitimate sibling would send, and see what one is sent.
 */
function otherTab(): { post(message: unknown): void; readonly received: unknown[] } {
  const channel = new FakeBroadcastChannel(SUPPORT_SESSION_BROADCAST_CHANNEL);
  const received: unknown[] = [];
  channel.addEventListener('message', (event: MessageEvent) => received.push(event.data));
  return { post: (message) => channel.postMessage(message), received };
}

describe('SupportSessionService (issue #553)', () => {
  beforeEach(() => installBroadcastChannel());

  afterEach(() => removeBroadcastChannel());

  describe('starting a session', () => {
    it('posts the reason to the business, publishes the banner state and the id for the interceptor', async () => {
      const { service, post, states } = harness();
      const observed = states();

      await firstValueFrom(service.start(9, 'Investigating a reported bug'));

      expect(post).toHaveBeenCalledWith('/api/super-admin/support-sessions/9', { reason: 'Investigating a reported bug' });
      expect(observed).toEqual([
        { kind: 'inactive' },
        { kind: 'viewing', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' }
      ]);
      expect(service.currentSessionId()).toBe('session-abc');
    });

    it('clears cached business data, refreshes capabilities and navigates to the dashboard', async () => {
      const { service, businessReset, accessRefresh, navigate } = harness();

      await firstValueFrom(service.start(9, 'Investigating a reported bug'));

      expect(businessReset).toHaveBeenCalledTimes(1);
      expect(accessRefresh).toHaveBeenCalledTimes(1);
      expect(navigate).toHaveBeenCalledWith(['/'], DASHBOARD_NAVIGATION);
    });

    it('never writes the session id to localStorage, sessionStorage or the URL', async () => {
      const localSet = jest.spyOn(Storage.prototype, 'setItem');
      const originalPushState = window.history.pushState.bind(window.history);
      const pushState = jest.spyOn(window.history, 'pushState').mockImplementation(originalPushState);
      const { service } = harness();

      await firstValueFrom(service.start(9, 'Investigating a reported bug'));

      expect(localSet).not.toHaveBeenCalled();
      expect(pushState).not.toHaveBeenCalled();
      expect(window.location.href).not.toContain('session-abc');

      localSet.mockRestore();
      pushState.mockRestore();
    });

    it('shares the id with another open tab through BroadcastChannel', async () => {
      const tabA = harness();
      const tabB = harness({ granted: false });
      const observedB = tabB.states();

      await firstValueFrom(tabA.service.start(9, 'Investigating a reported bug'));

      expect(observedB).toEqual([
        { kind: 'inactive' },
        { kind: 'viewing', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' }
      ]);
      expect(tabB.service.currentSessionId()).toBe('session-abc');
    });
  });

  /** What `active$` publishes for every page's write controls (issue #554). */
  describe('active$', () => {
    it('starts false and reports true once a session has started, with no duplicate for an unchanged state', async () => {
      const { service } = harness();
      const observed: boolean[] = [];
      service.active$.subscribe((active) => observed.push(active));

      await firstValueFrom(service.start(9, 'Investigating a reported bug'));

      expect(observed).toEqual([false, true]);
    });

    it('reports false again once the session has ended', async () => {
      const { service } = harness();
      const observed: boolean[] = [];
      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      service.active$.subscribe((active) => observed.push(active));

      await firstValueFrom(service.end());

      expect(observed).toEqual([true, false]);
    });
  });

  describe('ending a session', () => {
    it('ends the caller’s own session, clears the id and the banner, and applies the business context change', async () => {
      const { service, post, businessReset, accessRefresh, navigate, states } = harness();
      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      const observed = states();
      businessReset.mockClear();
      accessRefresh.mockClear();
      navigate.mockClear();

      await firstValueFrom(service.end());

      expect(post).toHaveBeenCalledWith('/api/super-admin/support-sessions/active/end', {});
      expect(service.currentSessionId()).toBeNull();
      expect(observed[observed.length - 1]).toEqual({ kind: 'inactive' });
      expect(businessReset).toHaveBeenCalledTimes(1);
      expect(accessRefresh).toHaveBeenCalledTimes(1);
      expect(navigate).toHaveBeenCalledWith(['/'], DASHBOARD_NAVIGATION);
    });

    it('ends the session in every other open tab (multi-tab end)', async () => {
      const tabA = harness();
      const tabB = harness({ granted: false });
      await firstValueFrom(tabA.service.start(9, 'Investigating a reported bug'));
      tabB.states();
      // The shared start is a context transition in this tab too (issue #670), so what the end
      // does here is counted from after it.
      tabB.businessReset.mockClear();
      tabB.accessRefresh.mockClear();
      tabB.navigate.mockClear();

      await firstValueFrom(tabA.service.end());

      expect(tabB.service.currentSessionId()).toBeNull();
      expect(tabB.businessReset).toHaveBeenCalledTimes(1);
      expect(tabB.accessRefresh).toHaveBeenCalledTimes(1);
      expect(tabB.navigate).toHaveBeenCalledWith(['/'], DASHBOARD_NAVIGATION);
    });
  });

  describe('the "support_session_ended" signal', () => {
    it('clears the session and applies the business context change once', async () => {
      const { service, businessReset, accessRefresh, navigate } = harness();
      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      businessReset.mockClear();
      accessRefresh.mockClear();
      navigate.mockClear();

      service.handleSessionEndedSignal();
      service.handleSessionEndedSignal();

      expect(service.currentSessionId()).toBeNull();
      expect(businessReset).toHaveBeenCalledTimes(1);
      expect(accessRefresh).toHaveBeenCalledTimes(1);
      expect(navigate).toHaveBeenCalledTimes(1);
    });

    it('does nothing for a tab that was never active', () => {
      const { service, businessReset, navigate } = harness();

      service.handleSessionEndedSignal();

      expect(service.currentSessionId()).toBeNull();
      expect(businessReset).not.toHaveBeenCalled();
      expect(navigate).not.toHaveBeenCalled();
    });
  });

  describe('a new or reloaded tab (issue #553: "a new tab asks the server\'s get-active endpoint")', () => {
    it('reports the administrator’s own active session as unresolved, never as a support view', async () => {
      const { service, get, isGranted, states } = harness({ activeOnServer: ACTIVE_ON_SERVER });

      // The boot check resolves synchronously here (every double answers with `of(...)`), so a
      // subscriber that starts after it has already run - the realistic case, since nothing
      // subscribes to `state$` before the banner does - only ever observes the settled answer, not
      // a transient `{ kind: 'inactive' }` on the way to it.
      const observed = states();

      expect(isGranted).toHaveBeenCalledTimes(1);
      expect(get).toHaveBeenCalledWith('/api/super-admin/support-sessions/active');
      // The active read never answers the id (issue #550's own design), so this tab could only
      // send requests as the administrator themselves: it is `unresolved`, not a support view
      // (issue #668).
      expect(observed).toEqual([{ kind: 'unresolved', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' }]);
      expect(service.currentSessionId()).toBeNull();
    });

    it('never asks the server at all for a caller who is not the configured platform administrator', () => {
      const { get, isGranted, states } = harness({ granted: false });

      states();

      expect(isGranted).toHaveBeenCalledTimes(1);
      expect(get).not.toHaveBeenCalled();
    });

    it('shows nothing when the administrator has no active session', async () => {
      const { states } = harness({ activeOnServer: null });

      const observed = states();
      await Promise.resolve();

      expect(observed).toEqual([{ kind: 'inactive' }]);
    });

    it('asks the server only once, however many subscribers the banner and other pages start', () => {
      const { get, states } = harness({ activeOnServer: null });

      states();
      states();

      expect(get).toHaveBeenCalledTimes(1);
    });
  });

  /**
   * The unresolved state (issue #668): a session is active on the server for this administrator,
   * and this tab cannot present its id. Everything here is about that tab never acting as though it
   * were inside the session, and never trying to get hold of the id by another route.
   */
  describe('a reloaded tab with no usable session id (issue #668)', () => {
    it('reports the session as active to every write control, so none is offered', () => {
      const { service } = harness({ activeOnServer: ACTIVE_ON_SERVER });
      const observed: boolean[] = [];

      service.active$.subscribe((active) => observed.push(active));

      expect(observed).toEqual([true]);
    });

    it('never adopts, recovers or stores an id, whatever the active read carries', () => {
      const localSet = jest.spyOn(Storage.prototype, 'setItem');
      const originalPushState = window.history.pushState.bind(window.history);
      const pushState = jest.spyOn(window.history, 'pushState').mockImplementation(originalPushState);
      // The contract does not answer the id at all; this double answers one anyway, because what
      // must hold is that the client does not read one from here even if it appears.
      const { service, post, states } = harness({
        activeOnServer: { ...ACTIVE_ON_SERVER, sessionId: 'session-from-the-probe' }
      });

      states();

      expect(service.currentSessionId()).toBeNull();
      expect(localSet).not.toHaveBeenCalled();
      expect(pushState).not.toHaveBeenCalled();
      expect(window.location.href).not.toContain('session-from-the-probe');
      // Nor does it quietly start a replacement session: a new session is the explicit start flow.
      expect(post).not.toHaveBeenCalled();

      localSet.mockRestore();
      pushState.mockRestore();
    });

    it('stays in ordinary context until a delayed probe answers, then reports it unresolved', () => {
      const answer = new Subject<unknown>();
      const { service, states } = harness({ get: jest.fn(() => answer) });
      const observed = states();

      expect(observed).toEqual([{ kind: 'inactive' }]);

      answer.next(ACTIVE_ON_SERVER);

      expect(observed).toEqual([
        { kind: 'inactive' },
        { kind: 'unresolved', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' }
      ]);
      expect(service.currentSessionId()).toBeNull();
    });

    it('leaves a support view another tab already shared alone when a delayed probe answers after it', async () => {
      const answer = new Subject<unknown>();
      const tabA = harness();
      const tabB = harness({ get: jest.fn(() => answer) });
      const observedB = tabB.states();

      await firstValueFrom(tabA.service.start(9, 'Investigating a reported bug'));
      answer.next(ACTIVE_ON_SERVER);

      expect(observedB[observedB.length - 1]).toEqual({
        kind: 'viewing',
        businessName: 'Snack Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      });
      expect(tabB.service.currentSessionId()).toBe('session-abc');
    });

    it('recovers to a support view when another tab shares the id it holds', async () => {
      const tabA = harness({ activeOnServer: null });
      const tabB = harness({ activeOnServer: ACTIVE_ON_SERVER });
      const observedB = tabB.states();

      await firstValueFrom(tabA.service.start(9, 'Investigating a reported bug'));

      expect(observedB).toEqual([
        { kind: 'unresolved', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' },
        { kind: 'viewing', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' }
      ]);
      expect(tabB.service.currentSessionId()).toBe('session-abc');
    });

    it('returns to ordinary context when the session is ended from it', async () => {
      const { service, post, businessReset, accessRefresh, navigate, states } = harness({
        activeOnServer: ACTIVE_ON_SERVER,
        post: jest.fn(() => of(undefined))
      });
      const observed = states();

      await firstValueFrom(service.end());

      expect(post).toHaveBeenCalledWith('/api/super-admin/support-sessions/active/end', {});
      expect(observed[observed.length - 1]).toEqual({ kind: 'inactive' });
      expect(businessReset).toHaveBeenCalledTimes(1);
      expect(accessRefresh).toHaveBeenCalledTimes(1);
      expect(navigate).toHaveBeenCalledWith(['/'], DASHBOARD_NAVIGATION);
    });

    it('stays unresolved when ending fails, rather than falling back to ordinary context', async () => {
      const { service, businessReset, navigate, states } = harness({
        activeOnServer: ACTIVE_ON_SERVER,
        post: jest.fn(() => throwError(() => ({ status: 500 })))
      });
      const observed = states();

      await expect(firstValueFrom(service.end())).rejects.toEqual({ status: 500 });

      expect(observed[observed.length - 1]).toEqual({
        kind: 'unresolved',
        businessName: 'Snack Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      });
      expect(businessReset).not.toHaveBeenCalled();
      expect(navigate).not.toHaveBeenCalled();
    });

    it('applies another tab’s end, so nothing is left blocked once the session is over', async () => {
      const tabA = harness();
      const tabB = harness({ activeOnServer: ACTIVE_ON_SERVER });
      const observedB = tabB.states();
      await firstValueFrom(tabA.service.start(9, 'Investigating a reported bug'));

      await firstValueFrom(tabA.service.end());

      expect(observedB[observedB.length - 1]).toEqual({ kind: 'inactive' });
      expect(tabB.service.currentSessionId()).toBeNull();
    });
  });

  /**
   * The request context `supportSessionInterceptor` captures per request (issue #669). What makes it
   * more than `currentSessionId()` is the generation: two contexts whose id is the same - ordinary
   * context before and after a whole session, the same business under a replacement session - are
   * still two contexts, and an answer from the first belongs to neither.
   */
  describe('the request context generation (issue #669)', () => {
    it('reports the current id, and stays current while nothing changes', async () => {
      const { service } = harness();
      await firstValueFrom(service.start(9, 'Investigating a reported bug'));

      const captured = service.captureRequestContext();

      expect(captured.sessionId).toBe('session-abc');
      expect(service.isCurrentContext(captured)).toBe(true);
      expect(service.isCurrentContext(service.captureRequestContext())).toBe(true);
    });

    it('supersedes an ordinary context when a session starts', async () => {
      const { service } = harness();
      const captured = service.captureRequestContext();

      await firstValueFrom(service.start(9, 'Investigating a reported bug'));

      expect(captured.sessionId).toBeNull();
      expect(service.isCurrentContext(captured)).toBe(false);
    });

    it('supersedes a support context when the session ends', async () => {
      const { service } = harness();
      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      const captured = service.captureRequestContext();

      await firstValueFrom(service.end());

      expect(service.isCurrentContext(captured)).toBe(false);
    });

    it('supersedes an ordinary context a whole session has been started and ended since', async () => {
      const { service } = harness();
      const captured = service.captureRequestContext();

      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      await firstValueFrom(service.end());

      // The id is `null` at both ends, which is why the generation has to be the thing compared.
      expect(service.currentSessionId()).toBe(captured.sessionId);
      expect(service.isCurrentContext(captured)).toBe(false);
    });

    it('supersedes the context on the `support_session_ended` signal', async () => {
      const { service } = harness();
      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      const captured = service.captureRequestContext();

      service.handleSessionEndedSignal();

      expect(service.isCurrentContext(captured)).toBe(false);
    });

    it('supersedes the context when another tab shares a session', async () => {
      const tabA = harness();
      const tabB = harness({ granted: false });
      const captured = tabB.service.captureRequestContext();

      await firstValueFrom(tabA.service.start(9, 'Investigating a reported bug'));

      expect(tabB.service.isCurrentContext(captured)).toBe(false);
    });

    it('supersedes the context when another tab ends the session', async () => {
      const tabA = harness();
      const tabB = harness({ granted: false });
      await firstValueFrom(tabA.service.start(9, 'Investigating a reported bug'));
      const captured = tabB.service.captureRequestContext();

      await firstValueFrom(tabA.service.end());

      expect(tabB.service.isCurrentContext(captured)).toBe(false);
    });

    /** A tab that learns it cannot present the session renders no business page (issue #668). */
    it('supersedes the context when the boot probe reports an unresolved session', () => {
      const answer = new Subject<unknown>();
      const { service, states } = harness({ get: jest.fn(() => answer) });
      states();
      const captured = service.captureRequestContext();

      answer.next(ACTIVE_ON_SERVER);

      expect(service.isCurrentContext(captured)).toBe(false);
    });

    it('leaves the context alone when the probe reports no session at all', () => {
      const answer = new Subject<unknown>();
      const { service, states } = harness({ get: jest.fn(() => answer) });
      states();
      const captured = service.captureRequestContext();

      answer.next(null);

      expect(service.isCurrentContext(captured)).toBe(true);
    });

    it('supersedes the context when the signed-in identity changes', () => {
      const { service, identity } = harness();
      const captured = service.captureRequestContext();

      identity.switchAccount('member-2');

      expect(service.isCurrentContext(captured)).toBe(false);
    });

    it('supersedes the context on a sign-out', () => {
      const { service, identity } = harness();
      const captured = service.captureRequestContext();

      identity.signIn(null);
      identity.raise(EventType.LOGOUT_SUCCESS);

      expect(service.isCurrentContext(captured)).toBe(false);
    });

    /** One sign-in reports itself twice, and neither report is a change of identity. */
    it('leaves the context alone when a notification reports the same identity', () => {
      const { service, identity } = harness();
      const captured = service.captureRequestContext();

      identity.raise(EventType.LOGIN_SUCCESS);
      identity.inProgress$.next(InteractionStatus.HandleRedirect);
      identity.inProgress$.next(InteractionStatus.None);

      expect(service.isCurrentContext(captured)).toBe(true);
    });

    /**
     * Learning an identity where there was none is not a change: a request issued before anybody is
     * signed in waits for MSAL's token before it is sent, so invalidating here would discard the
     * first answer of every page loaded straight into a sign-in redirect.
     */
    it('leaves the context alone when the first identity signs in', () => {
      const { service, identity } = harness({ signedIn: null });
      const captured = service.captureRequestContext();

      identity.switchAccount('member-1');

      expect(service.isCurrentContext(captured)).toBe(true);
    });
  });

  /**
   * One transition path for every effective context change, and the window it reports around itself
   * (issue #670). The shell withholds the routed page for that window, which is what takes the
   * previous business's data off screen and brings a page back that reads the new context from
   * scratch; `support-session-context-transition.spec.ts` proves that over real rendering.
   */
  describe('the business-context transition (issue #670)', () => {
    it('reports the transition window around the dashboard navigation', async () => {
      const navigated = new Subject<boolean>();
      const navigate = jest.fn(() => firstValueFrom(navigated));
      const { service } = harness({ navigate });
      const observed: boolean[] = [];
      service.contextChanging$.subscribe((changing) => observed.push(changing));

      await firstValueFrom(service.start(9, 'Investigating a reported bug'));

      // Still changing: the page stays withheld until the navigation for the new context settles.
      expect(observed).toEqual([false, true]);

      navigated.next(true);
      navigated.complete();
      await Promise.resolve();

      expect(observed).toEqual([false, true, false]);
      expect(navigate).toHaveBeenCalledWith(['/'], DASHBOARD_NAVIGATION);
    });

    it('releases the page even when the navigation is refused', async () => {
      const navigate = jest.fn(() => Promise.reject(new Error('a guard threw')));
      const { service } = harness({ navigate });
      const observed: boolean[] = [];
      service.contextChanging$.subscribe((changing) => observed.push(changing));

      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      await Promise.resolve();

      expect(observed).toEqual([false, true, false]);
    });

    it('keeps the page withheld until the last of two overlapping transitions settles', async () => {
      const first = new Subject<boolean>();
      const second = new Subject<boolean>();
      const pending = [first, second];
      const navigate = jest.fn(() => firstValueFrom(pending.shift() ?? first));
      const { service } = harness({ navigate });
      const observed: boolean[] = [];
      service.contextChanging$.subscribe((changing) => observed.push(changing));

      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      service.handleSessionEndedSignal();
      first.next(true);
      first.complete();
      await Promise.resolve();

      expect(observed).toEqual([false, true]);

      second.next(true);
      second.complete();
      await Promise.resolve();

      expect(observed).toEqual([false, true, false]);
    });
  });

  /**
   * That the effective business itself changed, once per transition (issue #671). The shell reads
   * the business and publishes its time zone from this, which the settling window above cannot say:
   * two overlapping transitions share one window and are two different businesses.
   */
  describe('reporting the effective business change (issue #671)', () => {
    const SHARED_SESSION = {
      type: 'started',
      accountKey: ACCOUNT_KEY,
      sessionId: 'session-from-the-other-tab',
      businessName: 'Snack Co',
      expiresAtUtc: '2026-02-01T01:00:00Z'
    };

    /** Every effective change, with whatever has to be in place first and then what does it. */
    const effectiveChanges: readonly [
      string,
      (harness: Harness) => Promise<void>,
      (harness: Harness) => Promise<void> | void
    ][] = [
      ['a session started here', () => Promise.resolve(), ({ service }) => firstValueFrom(service.start(9, 'A reason'))],
      [
        'the session ended here',
        ({ service }) => firstValueFrom(service.start(9, 'A reason')).then(() => undefined),
        ({ service }) => firstValueFrom(service.end())
      ],
      [
        'a call in the current session refused as ended',
        ({ service }) => firstValueFrom(service.start(9, 'A reason')).then(() => undefined),
        ({ service }) => service.handleSessionEndedSignal()
      ],
      ['another tab sharing its session', () => Promise.resolve(), () => otherTab().post(SHARED_SESSION)],
      [
        'another tab ending it',
        ({ service }) => firstValueFrom(service.start(9, 'A reason')).then(() => undefined),
        () => otherTab().post({ type: 'ended', accountKey: ACCOUNT_KEY })
      ]
    ];

    it.each(effectiveChanges)('reports %s', async (_case, arrange, transition) => {
      const subject = harness();
      subject.states();
      await arrange(subject);
      let changes = 0;
      subject.service.contextChanged$.subscribe(() => changes++);

      await transition(subject);

      expect(changes).toBe(1);
    });

    it('reports nothing while no effective context has changed', () => {
      const { service } = harness();
      let changes = 0;
      service.contextChanged$.subscribe(() => changes++);

      // Neither an idle tab nor a message it must ignore is a business-context change.
      otherTab().post({ type: 'started', accountKey: 'somebody-else', sessionId: 'x', businessName: 'Snack Co' });
      service.handleSessionEndedSignal();

      expect(changes).toBe(0);
    });

    it('reports each of two overlapping transitions, which share one settling window', async () => {
      const navigate = jest.fn(() => new Promise<boolean>(() => undefined));
      const { service } = harness({ navigate });
      const changing: boolean[] = [];
      let changes = 0;
      service.contextChanging$.subscribe((value) => changing.push(value));
      service.contextChanged$.subscribe(() => changes++);

      await firstValueFrom(service.start(9, 'A reason'));
      service.handleSessionEndedSignal();

      expect(changing).toEqual([false, true]);
      expect(changes).toBe(2);
    });

    it('reports the change after the cached business is dropped, so the read that follows is the new one', async () => {
      const { service, businessReset } = harness();
      const resetsWhenReported: number[] = [];
      service.contextChanged$.subscribe(() => resetsWhenReported.push(businessReset.mock.calls.length));

      await firstValueFrom(service.start(9, 'A reason'));

      expect(resetsWhenReported).toEqual([1]);
    });
  });

  /**
   * A session's own expiry, which every tab that knows the session reaches on its own (issue #670).
   * Before this, the expiry only became visible when the server refused the next call, so a tab
   * nobody touched kept a finished session's banner and its business's figures on screen.
   */
  describe('the session expiring (issue #670)', () => {
    beforeEach(() => jest.useFakeTimers().setSystemTime(Date.parse('2026-02-01T00:00:00Z')));

    afterEach(() => jest.useRealTimers());

    it('ends the local view at the expiry, through the same transition', async () => {
      const { service, businessReset, accessRefresh, navigate, states } = harness();
      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      const observed = states();
      businessReset.mockClear();
      accessRefresh.mockClear();
      navigate.mockClear();

      jest.advanceTimersByTime(60 * 60 * 1000);

      expect(service.currentSessionId()).toBeNull();
      expect(observed[observed.length - 1]).toEqual({ kind: 'inactive' });
      expect(businessReset).toHaveBeenCalledTimes(1);
      expect(accessRefresh).toHaveBeenCalledTimes(1);
      expect(navigate).toHaveBeenCalledWith(['/'], DASHBOARD_NAVIGATION);
    });

    it('leaves the view alone until the expiry is actually reached', async () => {
      const { service, navigate } = harness();
      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      navigate.mockClear();

      jest.advanceTimersByTime(59 * 60 * 1000);

      expect(service.currentSessionId()).toBe('session-abc');
      expect(navigate).not.toHaveBeenCalled();
    });

    /** A tab that holds no id still knows when the session it was told about is over. */
    it('releases a tab that could not present the session', () => {
      const { states } = harness({ activeOnServer: ACTIVE_ON_SERVER });
      const observed = states();

      jest.advanceTimersByTime(60 * 60 * 1000);

      expect(observed[observed.length - 1]).toEqual({ kind: 'inactive' });
    });

    /** Every tab schedules the same instant from the same state, so none has to be told. */
    it('tells no other tab, because each of them ends at the same instant on its own', async () => {
      const sibling = otherTab();
      const { service } = harness();
      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      expect(sibling.received).toHaveLength(1);

      jest.advanceTimersByTime(60 * 60 * 1000);

      expect(sibling.received).toHaveLength(1);
    });

    it('schedules nothing from an expiry this browser’s clock says has already passed', async () => {
      jest.setSystemTime(Date.parse('2026-02-01T02:00:00Z'));
      const { service, navigate } = harness();

      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      navigate.mockClear();
      jest.advanceTimersByTime(24 * 60 * 60 * 1000);

      // The browser's clock is not the authority on a session's lifetime: the server's
      // `support_session_ended` refusal is, and a clock hours fast must not end a fresh session.
      expect(service.currentSessionId()).toBe('session-abc');
      expect(navigate).not.toHaveBeenCalled();
    });

    it('schedules nothing from an unreadable expiry', async () => {
      const { service, navigate } = harness({
        post: jest.fn(() => of({ ...STARTED_RESPONSE, expiresAtUtc: 'not an instant' }))
      });

      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      navigate.mockClear();
      jest.advanceTimersByTime(24 * 60 * 60 * 1000);

      expect(service.currentSessionId()).toBe('session-abc');
      expect(navigate).not.toHaveBeenCalled();
    });
  });

  /**
   * What arrives over `BroadcastChannel` is a message from another tab of this origin carrying a
   * session **id** - the one credential this application holds - so nothing about it is assumed
   * (issue #670). A legitimate sibling never sends a malformed or another identity's message, which
   * is why these tests post the messages themselves rather than driving a second service instance.
   */
  describe('a message from another tab (issue #670)', () => {
    it('names the identity the broadcast session belongs to', async () => {
      const sibling = otherTab();
      const { service } = harness();

      await firstValueFrom(service.start(9, 'Investigating a reported bug'));

      expect(sibling.received).toEqual([
        {
          type: 'started',
          accountKey: ACCOUNT_KEY,
          sessionId: 'session-abc',
          businessName: 'Snack Co',
          expiresAtUtc: '2026-02-01T01:00:00Z'
        }
      ]);
    });

    it('applies the same transition a local start does', () => {
      const sibling = otherTab();
      const { service, businessReset, accessRefresh, navigate, states } = harness({ granted: false });
      const observed = states();

      sibling.post({
        type: 'started',
        accountKey: ACCOUNT_KEY,
        sessionId: 'session-abc',
        businessName: 'Snack Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      });

      expect(service.currentSessionId()).toBe('session-abc');
      expect(observed[observed.length - 1]).toEqual({
        kind: 'viewing',
        businessName: 'Snack Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      });
      // Issue #553 moved only the id and the banner here, which left the previous business's
      // cached business, capabilities and page in place in every tab but the one that started it.
      expect(businessReset).toHaveBeenCalledTimes(1);
      expect(accessRefresh).toHaveBeenCalledTimes(1);
      expect(navigate).toHaveBeenCalledWith(['/'], DASHBOARD_NAVIGATION);
    });

    it('never adopts a session another signed-in identity started', () => {
      const sibling = otherTab();
      const { service, businessReset, navigate, states } = harness({ granted: false });
      const observed = states();

      sibling.post({
        type: 'started',
        accountKey: 'another-administrator',
        sessionId: 'session-abc',
        businessName: 'Snack Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      });

      expect(service.currentSessionId()).toBeNull();
      expect(observed).toEqual([{ kind: 'inactive' }]);
      expect(businessReset).not.toHaveBeenCalled();
      expect(navigate).not.toHaveBeenCalled();
    });

    it.each([
      ['no identity at all', { type: 'started', sessionId: 'session-abc', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' }],
      ['an empty session id', { type: 'started', accountKey: ACCOUNT_KEY, sessionId: '', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' }],
      ['a session id that is not a string', { type: 'started', accountKey: ACCOUNT_KEY, sessionId: 7, businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' }],
      ['no expiry', { type: 'started', accountKey: ACCOUNT_KEY, sessionId: 'session-abc', businessName: 'Snack Co' }],
      ['no business name', { type: 'started', accountKey: ACCOUNT_KEY, sessionId: 'session-abc', expiresAtUtc: '2026-02-01T01:00:00Z' }],
      ['an unknown kind', { type: 'resumed', accountKey: ACCOUNT_KEY, sessionId: 'session-abc', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' }],
      ['nothing of the shape at all', 'session-abc'],
      ['null', null]
    ])('never adopts a session from a message with %s', (_case, message) => {
      const sibling = otherTab();
      const { service, navigate, states } = harness({ granted: false });
      const observed = states();

      sibling.post(message);

      expect(service.currentSessionId()).toBeNull();
      expect(observed).toEqual([{ kind: 'inactive' }]);
      expect(navigate).not.toHaveBeenCalled();
    });

    it('applies a same-identity end', async () => {
      const sibling = otherTab();
      const { service, states } = harness();
      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      const observed = states();

      sibling.post({ type: 'ended', accountKey: ACCOUNT_KEY });

      expect(service.currentSessionId()).toBeNull();
      expect(observed[observed.length - 1]).toEqual({ kind: 'inactive' });
    });

    it.each([
      ['another identity', { type: 'ended', accountKey: 'another-administrator' }],
      ['no identity', { type: 'ended' }],
      ['an unknown kind', { type: 'finished', accountKey: ACCOUNT_KEY }]
    ])('leaves this tab’s session alone for an end from %s', async (_case, message) => {
      const sibling = otherTab();
      const { service, states } = harness();
      await firstValueFrom(service.start(9, 'Investigating a reported bug'));
      const observed = states();

      sibling.post(message);

      expect(service.currentSessionId()).toBe('session-abc');
      expect(observed[observed.length - 1]).toEqual({
        kind: 'viewing',
        businessName: 'Snack Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      });
    });
  });
});
