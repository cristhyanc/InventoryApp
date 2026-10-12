import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { ToastService } from '../services/toast.service';
import { isAccountStateBypassRoute, memberForbiddenNoticeInterceptor } from './member-forbidden-notice.interceptor';

interface Harness {
  http: HttpClient;
  httpMock: HttpTestingController;
  error: jest.Mock;
}

function configure(currentUrl: string): Harness {
  const error = jest.fn();

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([memberForbiddenNoticeInterceptor])),
      provideHttpClientTesting(),
      { provide: ToastService, useValue: { error } },
      { provide: Router, useValue: { url: currentUrl } }
    ]
  });

  return {
    http: TestBed.inject(HttpClient),
    httpMock: TestBed.inject(HttpTestingController),
    error
  };
}

function refuse(harness: Harness, url: string, status: number): void {
  harness.http.get(url).subscribe({ error: () => undefined });
  harness.httpMock.expectOne(url).flush('refused', { status, statusText: 'Forbidden' });
}

/**
 * The global `403` handler (issue #529): a member endpoint refusing a request shows "you don't
 * have access to this", except on a route a person without a usable membership may legitimately
 * be on, where the account-state routing or the page itself already explains the situation.
 */
describe('memberForbiddenNoticeInterceptor (issue #529)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
  });

  it('shows "you don\'t have access to this" for a 403 from a member endpoint on an ordinary route', () => {
    const harness = configure('/reports/bookkeeping');

    refuse(harness, '/api/reports/bookkeeping', 403);

    expect(harness.error).toHaveBeenCalledWith("You don't have access to this.");
  });

  /** AC: "a 403 on a bypassed route never triggers member handling". */
  it.each(['/welcome', '/application-pending', '/application-rejected', '/access-unavailable', '/invite/abc123'])(
    'never shows the notice while the current route is the bypassed route %s',
    (currentUrl) => {
      const harness = configure(currentUrl);

      refuse(harness, '/api/dashboard/summary', 403);

      expect(harness.error).not.toHaveBeenCalled();
    }
  );

  it('never reacts to a refusal of the capability read itself', () => {
    const harness = configure('/reports/bookkeeping');

    refuse(harness, '/api/me/access', 403);

    expect(harness.error).not.toHaveBeenCalled();
  });

  it('ignores every other failure status', () => {
    const harness = configure('/reports/bookkeeping');

    refuse(harness, '/api/reports/bookkeeping', 401);
    refuse(harness, '/api/reports/bookkeeping', 404);
    refuse(harness, '/api/reports/bookkeeping', 500);

    expect(harness.error).not.toHaveBeenCalled();
  });

  it('re-raises the refusal unchanged', () => {
    const harness = configure('/reports/bookkeeping');
    let status: number | undefined;
    harness.http.get('/api/reports/bookkeeping').subscribe({ error: (error) => (status = error.status) });
    harness.httpMock.expectOne('/api/reports/bookkeeping').flush('refused', { status: 403, statusText: 'Forbidden' });

    expect(status).toBe(403);
  });
});

describe('isAccountStateBypassRoute', () => {
  it('matches the account-state routing destinations exactly', () => {
    expect(isAccountStateBypassRoute('/welcome')).toBe(true);
    expect(isAccountStateBypassRoute('/application-pending')).toBe(true);
    expect(isAccountStateBypassRoute('/application-rejected')).toBe(true);
    expect(isAccountStateBypassRoute('/access-unavailable')).toBe(true);
  });

  it('matches any invite token by prefix', () => {
    expect(isAccountStateBypassRoute('/invite/abc123')).toBe(true);
    expect(isAccountStateBypassRoute('/invite/abc123?x=1')).toBe(true);
  });

  it('does not match an ordinary business route', () => {
    expect(isAccountStateBypassRoute('/')).toBe(false);
    expect(isAccountStateBypassRoute('/reports/bookkeeping')).toBe(false);
    expect(isAccountStateBypassRoute('/access-not-allowed')).toBe(false);
  });
});
