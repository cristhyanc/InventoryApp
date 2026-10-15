import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, EventMessage, InteractionStatus } from '@azure/msal-browser';
import { BehaviorSubject, Subject } from 'rxjs';
import { ConfigService } from '../services/config.service';
import { ToastService } from '../services/toast.service';
import { FORBIDDEN_INTERCEPTORS } from './forbidden-interceptors';

const ACCESS_PROBE = '/api/me/access';
const ACCOUNT_STATE_PROBE = '/api/me/account-state';
const DIAGNOSTICS_PROBE = '/api/admin/diagnostics/access';
/** The sidebar badge's own read of the Super Admin businesses surface (issue #673). */
const PENDING_COUNT = '/api/super-admin/businesses/pending-count';
const MEMBER_MESSAGE = "You don't have access to this.";

interface Harness {
  http: HttpClient;
  httpMock: HttpTestingController;
  /** Every `ToastService.error(...)` the chain raised. */
  toastError: jest.Mock;
}

/**
 * The production chain over the **real** `AccessService` and `AccountStateService`, so a refusal
 * drives the refresh reads those caches actually issue. Only MSAL, the configured API base URL,
 * the router's current URL and the toast are doubled: nothing else about the chain is simulated.
 */
function configure(currentUrl = '/reports/bookkeeping', apiBaseUrl = '/api'): Harness {
  const toastError = jest.fn();
  const signedIn = { homeAccountId: 'member-1' } as unknown as AccountInfo;

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([...FORBIDDEN_INTERCEPTORS])),
      provideHttpClientTesting(),
      { provide: ConfigService, useValue: { apiBaseUrl } },
      { provide: ToastService, useValue: { error: toastError } },
      { provide: Router, useValue: { url: currentUrl } },
      {
        provide: MsalService,
        useValue: {
          instance: { getActiveAccount: () => signedIn, getAllAccounts: () => [signedIn] }
        }
      },
      {
        provide: MsalBroadcastService,
        useValue: {
          inProgress$: new BehaviorSubject(InteractionStatus.None),
          msalSubject$: new Subject<EventMessage>()
        }
      }
    ]
  });

  return {
    http: TestBed.inject(HttpClient),
    httpMock: TestBed.inject(HttpTestingController),
    toastError
  };
}

/**
 * Issues `url` and returns a reader of the failure it eventually reports, so a test can flush the
 * refusal first and still assert that the original error reached the caller unchanged.
 */
function issue(harness: Harness, url: string): () => HttpErrorResponse | null {
  let failure: HttpErrorResponse | null = null;
  harness.http.get(url).subscribe({ error: (error: HttpErrorResponse) => (failure = error) });
  return (): HttpErrorResponse | null => failure;
}

/**
 * Refuses every outstanding request with `403`, repeatedly, until the chain stops issuing new
 * ones, and returns the URLs in the order they were refused. A chain that never settles is the
 * defect this file exists for, so the rounds are capped and exhausting the cap fails loudly
 * rather than hanging the suite.
 */
function refuseEverything(harness: Harness, rounds = 12): string[] {
  const refused: string[] = [];

  for (let round = 0; round < rounds; round++) {
    const outstanding = harness.httpMock.match(() => true);
    if (outstanding.length === 0) {
      return refused;
    }

    for (const outstandingRequest of outstanding) {
      refused.push(outstandingRequest.request.url);
      outstandingRequest.flush('refused', { status: 403, statusText: 'Forbidden' });
    }
  }

  throw new Error(`the chain was still issuing requests after ${rounds} rounds: ${refused.join(', ')}`);
}

/**
 * The composed `403` chain (issue #602). Each interceptor was tested on its own (issues #528,
 * #529), which is exactly why the loop between them was invisible: `accessForbiddenInterceptor`
 * excluded only its own probe and `accountStateForbiddenInterceptor` only its own, so a refused
 * account-state probe refreshed the capabilities, whose refused probe refreshed the account state,
 * and so on without end.
 */
describe('the composed forbidden-interceptor chain (issue #602)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
  });

  it('settles after one request when the account-state probe keeps being refused', () => {
    const harness = configure();

    harness.http.get(ACCOUNT_STATE_PROBE).subscribe({ error: () => undefined });

    expect(refuseEverything(harness)).toEqual([ACCOUNT_STATE_PROBE]);
    expect(harness.toastError).not.toHaveBeenCalled();
  });

  /**
   * The account-state refresh after a refused capability read is deliberately kept (issue #529):
   * `GET /api/me/access` requires membership, so its own refusal is a staleness signal. It is now
   * strictly one-way - the account-state probe's own refusal starts nothing - so the chain is a
   * three-step dead end instead of a cycle.
   */
  it('settles after the one-way account-state read when the capability probe keeps being refused', () => {
    const harness = configure();

    harness.http.get(ACCESS_PROBE).subscribe({ error: () => undefined });

    expect(refuseEverything(harness)).toEqual([ACCESS_PROBE, ACCOUNT_STATE_PROBE]);
    expect(harness.toastError).not.toHaveBeenCalled();
  });

  it('settles after four requests when a member endpoint and then both probes keep being refused', () => {
    const harness = configure();

    harness.http.get('/api/reports/bookkeeping').subscribe({ error: () => undefined });

    expect(refuseEverything(harness)).toEqual([
      '/api/reports/bookkeeping',
      // One coalesced refresh of each cache, then the one-way account-state read the refused
      // capability probe asks for.
      ACCOUNT_STATE_PROBE,
      ACCESS_PROBE,
      ACCOUNT_STATE_PROBE
    ]);
    expect(harness.toastError).toHaveBeenCalledTimes(1);
    expect(harness.toastError).toHaveBeenCalledWith(MEMBER_MESSAGE);
  });

  /** AC: "a 403 from a real member endpoint still coalesces access/account-state refreshes". */
  it('coalesces the refreshes of two concurrently refused member requests', () => {
    const harness = configure();

    harness.http.get('/api/reports/bookkeeping').subscribe({ error: () => undefined });
    harness.http.get('/api/reports/daily').subscribe({ error: () => undefined });

    const refused = refuseEverything(harness);

    expect(refused.filter((url) => url === ACCESS_PROBE)).toHaveLength(1);
    expect(refused).toEqual([
      '/api/reports/bookkeeping',
      '/api/reports/daily',
      ACCOUNT_STATE_PROBE,
      ACCESS_PROBE,
      ACCOUNT_STATE_PROBE
    ]);
  });

  /**
   * The platform diagnostics access probe is the sidebar's quiet "is this the configured platform
   * administrator" question (issue #335). A `403` is its expected answer for everybody else, says
   * nothing about a business membership, and must stay silent.
   */
  it('neither reports nor refreshes anything for a refused platform diagnostics probe', () => {
    const harness = configure('/dashboard');

    const failure = issue(harness, DIAGNOSTICS_PROBE);
    harness.httpMock.expectOne(DIAGNOSTICS_PROBE).flush('refused', { status: 403, statusText: 'Forbidden' });

    expect(harness.toastError).not.toHaveBeenCalled();
    expect(harness.httpMock.match(() => true)).toEqual([]);
    expect(failure()?.status).toBe(403);
  });

  /**
   * The Super Admin businesses surface (issue #673). The sidebar's badge reads
   * `GET .../pending-count` on its own, with no page behind it, so a refusal that refreshed the
   * member caches put one request per access emission back on the wire - and each refreshed
   * capability set asked the sidebar for the count again.
   */
  it.each([
    '/api/super-admin/businesses',
    PENDING_COUNT,
    `${PENDING_COUNT}?at=now`,
    `${PENDING_COUNT}/`,
    '/api/super-admin/businesses/7/members/19/release'
  ])('neither reports nor refreshes anything for a refused %s', (url) => {
    const harness = configure('/super-admin/businesses');

    const failure = issue(harness, url);

    expect(refuseEverything(harness)).toEqual([url]);
    expect(harness.toastError).not.toHaveBeenCalled();
    expect(failure()?.status).toBe(403);
  });

  it('classifies the Super Admin businesses surface on a deployed API origin too (issue #673)', () => {
    const harness = configure('/super-admin/businesses', 'https://api.example.test/api/');
    const url = `https://api.example.test/api${PENDING_COUNT.slice('/api'.length)}`;

    harness.http.get(url).subscribe({ error: () => undefined });

    expect(refuseEverything(harness)).toEqual([url]);
    expect(harness.toastError).not.toHaveBeenCalled();
  });

  it('never treats a request to another origin as a member endpoint', () => {
    const harness = configure('/dashboard');

    harness.http.get('https://login.microsoftonline.test/common/discovery').subscribe({ error: () => undefined });

    expect(refuseEverything(harness)).toEqual(['https://login.microsoftonline.test/common/discovery']);
    expect(harness.toastError).not.toHaveBeenCalled();
  });

  it('never treats a static asset as a member endpoint', () => {
    const harness = configure('/dashboard');

    harness.http.get('/assets/config.json').subscribe({ error: () => undefined });

    expect(refuseEverything(harness)).toEqual(['/assets/config.json']);
    expect(harness.toastError).not.toHaveBeenCalled();
  });

  /** AC: "query strings and trailing slashes do not defeat exclusions". */
  it.each([`${ACCOUNT_STATE_PROBE}?reason=refresh`, `${ACCOUNT_STATE_PROBE}/`])(
    'still recognises the account-state probe as %s',
    (probeUrl) => {
      const harness = configure();

      harness.http.get(probeUrl).subscribe({ error: () => undefined });

      expect(refuseEverything(harness)).toEqual([probeUrl]);
      expect(harness.toastError).not.toHaveBeenCalled();
    }
  );

  it.each([`${DIAGNOSTICS_PROBE}?x=1`, `${DIAGNOSTICS_PROBE}/`])(
    'still recognises the platform diagnostics probe as %s',
    (probeUrl) => {
      const harness = configure('/dashboard');

      harness.http.get(probeUrl).subscribe({ error: () => undefined });

      expect(refuseEverything(harness)).toEqual([probeUrl]);
      expect(harness.toastError).not.toHaveBeenCalled();
    }
  );

  it('classifies the same endpoints when the API is configured on a deployed origin', () => {
    const harness = configure('/dashboard', 'https://api.example.test/api/');

    harness.http.get('https://api.example.test/api/me/account-state').subscribe({ error: () => undefined });

    expect(refuseEverything(harness)).toEqual(['https://api.example.test/api/me/account-state']);
    expect(harness.toastError).not.toHaveBeenCalled();
  });

  /** AC: "relevant ordinary member 403s still show the existing message on member routes". */
  it('still shows the member message and re-raises the refusal on an ordinary route', () => {
    const harness = configure('/reports/bookkeeping');

    const failure = issue(harness, '/api/reports/bookkeeping');
    refuseEverything(harness);

    expect(harness.toastError).toHaveBeenCalledWith(MEMBER_MESSAGE);
    expect(failure()?.status).toBe(403);
  });

  /** AC: "account-state/invite route suppression ... preserved". */
  it.each(['/welcome', '/application-pending', '/application-rejected', '/access-unavailable', '/invite/abc123'])(
    'still suppresses the member message while the current route is %s',
    (currentUrl) => {
      const harness = configure(currentUrl);

      harness.http.get('/api/dashboard/summary').subscribe({ error: () => undefined });
      refuseEverything(harness);

      expect(harness.toastError).not.toHaveBeenCalled();
    }
  );

  it('still refreshes both caches from a member refusal on a suppressed route', () => {
    const harness = configure('/access-unavailable');

    harness.http.get('/api/dashboard/summary').subscribe({ error: () => undefined });

    expect(refuseEverything(harness)).toEqual([
      '/api/dashboard/summary',
      ACCOUNT_STATE_PROBE,
      ACCESS_PROBE,
      ACCOUNT_STATE_PROBE
    ]);
  });

  it('leaves the caches alone for a status that is not 403', () => {
    const harness = configure();

    harness.http.get('/api/reports/bookkeeping').subscribe({ error: () => undefined });
    harness.httpMock.expectOne('/api/reports/bookkeeping').flush('failed', { status: 500, statusText: 'Error' });

    expect(harness.httpMock.match(() => true)).toEqual([]);
    expect(harness.toastError).not.toHaveBeenCalled();
  });
});
