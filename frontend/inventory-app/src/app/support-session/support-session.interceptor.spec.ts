import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConfigService } from '../services/config.service';
import { supportSessionInterceptor } from './support-session.interceptor';
import { SUPPORT_SESSION_HEADER, SupportSessionService } from './support-session.service';

const API_BASE_URL = '/api';

interface Harness {
  http: HttpClient;
  httpMock: HttpTestingController;
  handleSessionEndedSignal: jest.Mock;
  /** Moves the effective support context to `id`, as starting, ending or replacing a session does. */
  setSessionId(id: string | null): void;
  /**
   * Moves the context on without changing the id, as a changed signed-in identity and a tab
   * learning it cannot present the session both do (issue #669).
   */
  invalidateContext(): void;
}

/**
 * The context double answers `captureRequestContext`/`isCurrentContext` exactly as
 * `SupportSessionService` does - the id the request presented, plus a generation every effective
 * context change moves on. The service's own invalidation triggers are its spec's subject;
 * `support-session-request-context.spec.ts` is where the two meet over the real service.
 */
function configure(): Harness {
  let sessionId: string | null = null;
  let generation = 0;
  const handleSessionEndedSignal = jest.fn();

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([supportSessionInterceptor])),
      provideHttpClientTesting(),
      { provide: ConfigService, useValue: { apiBaseUrl: API_BASE_URL } },
      {
        provide: SupportSessionService,
        useValue: {
          currentSessionId: () => sessionId,
          captureRequestContext: () => ({ sessionId, generation }),
          isCurrentContext: (context: { sessionId: string | null; generation: number }) =>
            context.generation === generation && context.sessionId === sessionId,
          handleSessionEndedSignal
        }
      }
    ]
  });

  return {
    http: TestBed.inject(HttpClient),
    httpMock: TestBed.inject(HttpTestingController),
    handleSessionEndedSignal,
    setSessionId: (id) => {
      sessionId = id;
      generation++;
    },
    invalidateContext: () => {
      generation++;
    }
  };
}

/**
 * `supportSessionInterceptor` (issues #553 and #669): attaches `X-Support-Session` while a session
 * is active, never to the control surface it is read from, and discards a response sent in a
 * business context that is no longer current by the time it arrives - whether that is this very
 * response answering `support_session_ended`, or something else having moved the context on while
 * it was in flight, including for a request that presented no id at all.
 */
describe('supportSessionInterceptor (issues #553 and #669)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
  });

  it('attaches the header while a session is active', () => {
    const harness = configure();
    harness.setSessionId('session-abc');

    harness.http.get('/api/dashboard/summary').subscribe();

    const request = harness.httpMock.expectOne('/api/dashboard/summary');
    expect(request.request.headers.get(SUPPORT_SESSION_HEADER)).toBe('session-abc');
    request.flush({});
  });

  it('sends no header at all while no session is active', () => {
    const harness = configure();

    harness.http.get('/api/dashboard/summary').subscribe();

    const request = harness.httpMock.expectOne('/api/dashboard/summary');
    expect(request.request.headers.has(SUPPORT_SESSION_HEADER)).toBe(false);
    request.flush({});
  });

  it.each([
    ['the start call', '/api/super-admin/support-sessions/9'],
    ['the active probe', '/api/super-admin/support-sessions/active'],
    ['the end call', '/api/super-admin/support-sessions/active/end'],
    ['the diagnostics probe', '/api/admin/diagnostics/access'],
    // Issue #673 classified these as platform-admin too. They are reached as the administrator and
    // the API ignores the header on them either way - `PlatformDiagnosticsEndpointAttribute` is
    // decided before a session is resolved - so withholding it changes nothing but says what is
    // true: a business decision is not something anybody makes from inside a support session.
    ['the businesses directory', '/api/super-admin/businesses'],
    ['the pending-count badge read', '/api/super-admin/businesses/pending-count'],
    ['a business decision', '/api/super-admin/businesses/9/approve']
  ])('never attaches the header to %s - the control surface, reached as the administrator', (_name, url) => {
    const harness = configure();
    harness.setSessionId('session-abc');

    harness.http.post(url, {}).subscribe();

    const request = harness.httpMock.expectOne(url);
    expect(request.request.headers.has(SUPPORT_SESSION_HEADER)).toBe(false);
    request.flush({});
  });

  it('leaves a static asset alone', () => {
    const harness = configure();
    harness.setSessionId('session-abc');

    harness.http.get('/assets/config.json').subscribe();

    const request = harness.httpMock.expectOne('/assets/config.json');
    expect(request.request.headers.has(SUPPORT_SESSION_HEADER)).toBe(false);
    request.flush({});
  });

  describe('a presented session that died mid-request', () => {
    it('reacts to `support_session_ended` on any call, and the caller sees neither a value nor an error', () => {
      const harness = configure();
      harness.setSessionId('session-abc');
      harness.handleSessionEndedSignal.mockImplementation(() => harness.setSessionId(null));
      const next = jest.fn();
      const error = jest.fn();
      const complete = jest.fn();

      harness.http.get('/api/dashboard/summary').subscribe({ next, error, complete });
      harness.httpMock
        .expectOne('/api/dashboard/summary')
        .flush({ code: 'support_session_ended' }, { status: 403, statusText: 'Forbidden' });

      expect(harness.handleSessionEndedSignal).toHaveBeenCalledTimes(1);
      expect(next).not.toHaveBeenCalled();
      expect(error).not.toHaveBeenCalled();
      expect(complete).toHaveBeenCalledTimes(1);
    });

    it('ignores a 403 that is not the session-ended code', () => {
      const harness = configure();
      harness.setSessionId('session-abc');
      const error = jest.fn();

      harness.http.get('/api/dashboard/summary').subscribe({ error });
      harness.httpMock
        .expectOne('/api/dashboard/summary')
        .flush({ code: 'read_only_support_session' }, { status: 403, statusText: 'Forbidden' });

      expect(harness.handleSessionEndedSignal).not.toHaveBeenCalled();
      expect(error).toHaveBeenCalledTimes(1);
      expect((error.mock.calls[0][0] as HttpErrorResponse).status).toBe(403);
    });

    it('leaves a request made with no session active untouched by a session-ended body', () => {
      const harness = configure();
      const error = jest.fn();

      harness.http.get('/api/dashboard/summary').subscribe({ error });
      harness.httpMock
        .expectOne('/api/dashboard/summary')
        .flush({ code: 'support_session_ended' }, { status: 403, statusText: 'Forbidden' });

      expect(harness.handleSessionEndedSignal).not.toHaveBeenCalled();
      expect(error).toHaveBeenCalledTimes(1);
    });
  });

  describe('a response sent under an id that is no longer current (stale response dropped)', () => {
    it('discards a successful response that arrives after the session moved on', () => {
      const harness = configure();
      harness.setSessionId('session-abc');
      const next = jest.fn();
      const complete = jest.fn();

      harness.http.get('/api/dashboard/summary').subscribe({ next, complete });
      // Something else - another tab's end, a different concurrent request - moved the session on
      // while this one was still in flight.
      harness.setSessionId(null);
      harness.httpMock.expectOne('/api/dashboard/summary').flush({ week: 1 });

      expect(next).not.toHaveBeenCalled();
      expect(complete).toHaveBeenCalledTimes(1);
    });

    it('discards an unrelated failure of a response sent under a superseded id too', () => {
      const harness = configure();
      harness.setSessionId('session-abc');
      const error = jest.fn();
      const complete = jest.fn();

      harness.http.get('/api/dashboard/summary').subscribe({ error, complete });
      harness.setSessionId('session-def');
      harness.httpMock.expectOne('/api/dashboard/summary').flush('server error', { status: 500, statusText: 'Error' });

      expect(error).not.toHaveBeenCalled();
      expect(complete).toHaveBeenCalledTimes(1);
    });

    it('leaves a response alone when the id has not changed since the request was sent', () => {
      const harness = configure();
      harness.setSessionId('session-abc');
      const next = jest.fn();

      harness.http.get('/api/dashboard/summary').subscribe({ next });
      harness.httpMock.expectOne('/api/dashboard/summary').flush({ week: 1 });

      expect(next).toHaveBeenCalledWith({ week: 1 });
    });
  });

  /**
   * The captured generation (issue #669). The id alone cannot see a context change that leaves it
   * as it was - an identity switch, a tab learning it holds no usable id - and cannot see anything
   * at all about a request sent in ordinary context.
   */
  describe('a response from a superseded context generation', () => {
    it('discards a successful response whose id never changed', () => {
      const harness = configure();
      harness.setSessionId('session-abc');
      const next = jest.fn();
      const complete = jest.fn();

      harness.http.get('/api/dashboard/summary').subscribe({ next, complete });
      harness.invalidateContext();
      harness.httpMock.expectOne('/api/dashboard/summary').flush({ week: 1 });

      expect(next).not.toHaveBeenCalled();
      expect(complete).toHaveBeenCalledTimes(1);
    });

    it('discards a response to a request that was sent with no session at all', () => {
      const harness = configure();
      const next = jest.fn();
      const error = jest.fn();
      const complete = jest.fn();

      harness.http.get('/api/dashboard/summary').subscribe({ next, error, complete });
      harness.invalidateContext();
      harness.httpMock.expectOne('/api/dashboard/summary').flush({ week: 1 });

      expect(next).not.toHaveBeenCalled();
      expect(error).not.toHaveBeenCalled();
      expect(complete).toHaveBeenCalledTimes(1);
    });

    it('discards a failure of a request that was sent with no session at all', () => {
      const harness = configure();
      const error = jest.fn();
      const complete = jest.fn();

      harness.http.get('/api/dashboard/summary').subscribe({ error, complete });
      harness.setSessionId('session-abc');
      harness.httpMock.expectOne('/api/dashboard/summary').flush('refused', { status: 403, statusText: 'Forbidden' });

      expect(error).not.toHaveBeenCalled();
      expect(complete).toHaveBeenCalledTimes(1);
    });
  });

  /**
   * The reaction is gated on the same comparison, and gated **before** it runs: a delayed refusal
   * of session A must not end session B, which is the defect issue #669 fixed by reordering the two.
   */
  describe('a delayed `support_session_ended` from a superseded session', () => {
    it('never ends the session that is current now', () => {
      const harness = configure();
      harness.setSessionId('session-abc');
      const error = jest.fn();
      const complete = jest.fn();

      harness.http.get('/api/dashboard/summary').subscribe({ error, complete });
      harness.setSessionId('session-def');
      harness.httpMock
        .expectOne('/api/dashboard/summary')
        .flush({ code: 'support_session_ended' }, { status: 403, statusText: 'Forbidden' });

      expect(harness.handleSessionEndedSignal).not.toHaveBeenCalled();
      expect(error).not.toHaveBeenCalled();
      expect(complete).toHaveBeenCalledTimes(1);
    });

    it('never ends the session when only the generation moved on', () => {
      const harness = configure();
      harness.setSessionId('session-abc');

      harness.http.get('/api/dashboard/summary').subscribe({ error: () => undefined });
      harness.invalidateContext();
      harness.httpMock
        .expectOne('/api/dashboard/summary')
        .flush({ code: 'support_session_ended' }, { status: 403, statusText: 'Forbidden' });

      expect(harness.handleSessionEndedSignal).not.toHaveBeenCalled();
    });
  });
});
