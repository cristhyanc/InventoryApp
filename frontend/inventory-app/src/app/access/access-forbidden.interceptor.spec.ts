import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConfigService } from '../services/config.service';
import { AccessService } from './access.service';
import { accessForbiddenInterceptor, isMemberAccessRefusal } from './access-forbidden.interceptor';

/** The dev-proxy API base URL `ConfigService` resolves locally; the deployed one is covered in `member-endpoint.spec.ts`. */
const API_BASE_URL = '/api';

interface Harness {
  http: HttpClient;
  httpMock: HttpTestingController;
  refreshUnlessReading: jest.Mock;
}

function configure(): Harness {
  const refreshUnlessReading = jest.fn();

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([accessForbiddenInterceptor])),
      provideHttpClientTesting(),
      { provide: AccessService, useValue: { refreshUnlessReading } },
      { provide: ConfigService, useValue: { apiBaseUrl: API_BASE_URL } }
    ]
  });

  return {
    http: TestBed.inject(HttpClient),
    httpMock: TestBed.inject(HttpTestingController),
    refreshUnlessReading
  };
}

function refuse(harness: Harness, url: string, status: number): HttpErrorResponse | null {
  let failure: HttpErrorResponse | null = null;
  harness.http.get(url).subscribe({ error: (error: HttpErrorResponse) => (failure = error) });
  harness.httpMock.expectOne(url).flush('refused', { status, statusText: 'Forbidden' });
  return failure;
}

/**
 * The "any `403` from a member endpoint" trigger on the capability cache (issue #528). It keeps
 * the cache current and nothing else: showing the operator "you don't have access to this" for a
 * refused member request is issue #529's global `403` handler.
 */
describe('accessForbiddenInterceptor (issue #528)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
  });

  it('re-reads the capabilities when a member endpoint refuses a request', () => {
    const harness = configure();

    refuse(harness, '/api/reports/bookkeeping', 403);

    expect(harness.refreshUnlessReading).toHaveBeenCalledTimes(1);
  });

  it('re-raises the refusal unchanged, so the page still reports its own failure', () => {
    const harness = configure();

    const failure = refuse(harness, '/api/reports/bookkeeping', 403);

    expect(failure?.status).toBe(403);
  });

  /**
   * A `403` from `GET /api/me/access` is the answer to this very question - reacting to it would
   * read it again, and again. `GET /api/me/account-state` is the other cache's own probe (issue
   * #602): reacting to it used to make the two caches refresh each other without end;
   * `forbidden-interceptor-chain.spec.ts` asserts the composed chain settles. The platform
   * diagnostics probe's `403` is the expected answer for every member (issue #602).
   */
  it.each([
    ['the capability read itself', '/api/me/access'],
    ['the account-state read', '/api/me/account-state'],
    ['the platform diagnostics probe', '/api/admin/diagnostics/access']
  ])('never reacts to a refusal of %s', (_name, url) => {
    const harness = configure();

    refuse(harness, url, 403);

    expect(harness.refreshUnlessReading).not.toHaveBeenCalled();
  });

  it('ignores every other failure status', () => {
    const harness = configure();

    refuse(harness, '/api/reports/bookkeeping', 401);
    refuse(harness, '/api/reports/bookkeeping', 404);
    refuse(harness, '/api/reports/bookkeeping', 500);

    expect(harness.refreshUnlessReading).not.toHaveBeenCalled();
  });

  it('leaves a successful request alone', () => {
    const harness = configure();
    harness.http.get('/api/reports/bookkeeping').subscribe();
    harness.httpMock.expectOne('/api/reports/bookkeeping').flush({});

    expect(harness.refreshUnlessReading).not.toHaveBeenCalled();
  });

  it('reacts once per refused request, so a page of refusals asks the service once each', () => {
    const harness = configure();

    refuse(harness, '/api/reports/bookkeeping', 403);
    refuse(harness, '/api/reports/daily', 403);

    expect(harness.refreshUnlessReading).toHaveBeenCalledTimes(2);
  });
});

describe('isMemberAccessRefusal', () => {
  it('excludes both cache probes, the platform diagnostics surface and the static assets', () => {
    expect(isMemberAccessRefusal('/api/me/access', API_BASE_URL)).toBe(false);
    expect(isMemberAccessRefusal('/api/me/access?refresh=1', API_BASE_URL)).toBe(false);
    expect(isMemberAccessRefusal('/api/me/account-state', API_BASE_URL)).toBe(false);
    expect(isMemberAccessRefusal('/api/admin/diagnostics/access', API_BASE_URL)).toBe(false);
    expect(isMemberAccessRefusal('/assets/config.json', API_BASE_URL)).toBe(false);
    expect(isMemberAccessRefusal('https://app.example.test/assets/config.json', API_BASE_URL)).toBe(false);
    expect(isMemberAccessRefusal('https://api.example.test/api/me/access', 'https://api.example.test/api')).toBe(false);
  });

  it('includes every other member endpoint', () => {
    expect(isMemberAccessRefusal('/api/dashboard/summary', API_BASE_URL)).toBe(true);
    expect(isMemberAccessRefusal('/api/suppliers/7/gst-defaults', API_BASE_URL)).toBe(true);
    expect(isMemberAccessRefusal('/api/admin/historical-gst-classification/preview', API_BASE_URL)).toBe(true);
  });
});
