import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AccountStateService } from './account-state.service';
import { accountStateForbiddenInterceptor, isMemberAccountStateRefusal } from './account-state-forbidden.interceptor';

interface Harness {
  http: HttpClient;
  httpMock: HttpTestingController;
  refreshUnlessReading: jest.Mock;
}

function configure(): Harness {
  const refreshUnlessReading = jest.fn();

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([accountStateForbiddenInterceptor])),
      provideHttpClientTesting(),
      { provide: AccountStateService, useValue: { refreshUnlessReading } }
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
 * The "any `403` from a member endpoint" trigger on the account-state cache (issue #529, applying
 * #528's trigger set to `AccountStateService`). It keeps the cache current and nothing else:
 * showing the operator "you don't have access to this" is `memberForbiddenNoticeInterceptor`'s job.
 */
describe('accountStateForbiddenInterceptor (issue #529)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
  });

  it('re-reads the account state when a member endpoint refuses a request', () => {
    const harness = configure();

    refuse(harness, '/api/reports/bookkeeping', 403);

    expect(harness.refreshUnlessReading).toHaveBeenCalledTimes(1);
  });

  /**
   * `GET /api/me/access` requires membership itself, so its own `403` is a signal the cached
   * account state may be stale too.
   */
  it('reacts to a refusal of the capability read', () => {
    const harness = configure();

    refuse(harness, '/api/me/access', 403);

    expect(harness.refreshUnlessReading).toHaveBeenCalledTimes(1);
  });

  it('never reacts to a refusal of the account-state read itself', () => {
    const harness = configure();

    refuse(harness, '/api/me/account-state', 403);

    expect(harness.refreshUnlessReading).not.toHaveBeenCalled();
  });

  it('ignores every other failure status', () => {
    const harness = configure();

    refuse(harness, '/api/reports/bookkeeping', 401);
    refuse(harness, '/api/reports/bookkeeping', 404);
    refuse(harness, '/api/reports/bookkeeping', 500);

    expect(harness.refreshUnlessReading).not.toHaveBeenCalled();
  });

  it('re-raises the refusal unchanged', () => {
    const harness = configure();

    const failure = refuse(harness, '/api/reports/bookkeeping', 403);

    expect(failure?.status).toBe(403);
  });
});

describe('isMemberAccountStateRefusal', () => {
  it('excludes the account-state read itself and the static assets', () => {
    expect(isMemberAccountStateRefusal('/api/me/account-state')).toBe(false);
    expect(isMemberAccountStateRefusal('https://api.example.test/api/me/account-state')).toBe(false);
    expect(isMemberAccountStateRefusal('/assets/config.json')).toBe(false);
  });

  it('includes every other member endpoint, including the capability read', () => {
    expect(isMemberAccountStateRefusal('/api/me/access')).toBe(true);
    expect(isMemberAccountStateRefusal('/api/dashboard/summary')).toBe(true);
  });
});
