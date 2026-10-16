import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { ConfigService } from '../services/config.service';
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
      { provide: Router, useValue: { url: currentUrl } },
      { provide: ConfigService, useValue: { apiBaseUrl: '/api' } }
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

function refuseWithCode(harness: Harness, url: string, code: string): void {
  harness.http.get(url).subscribe({ error: () => undefined });
  harness.httpMock.expectOne(url).flush({ code, message: 'refused' }, { status: 403, statusText: 'Forbidden' });
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

  /**
   * AC (issue #602): "a 403 from GET /api/admin/diagnostics/access neither displays the
   * member-access toast nor starts member-cache refreshes". The probe is the sidebar's quiet
   * platform-admin question (issue #335) and a refusal is its expected answer for every ordinary
   * member, on any route - the bypass-route list is not what keeps it quiet.
   */
  it.each([
    '/api/admin/diagnostics/access',
    '/api/admin/diagnostics/access?x=1',
    '/api/admin/diagnostics/query',
    '/api/me/account-state'
  ])('never reports a refusal of %s', (url) => {
    const harness = configure('/dashboard');

    refuse(harness, url, 403);

    expect(harness.error).not.toHaveBeenCalled();
  });

  it('never reports a refusal from another origin', () => {
    const harness = configure('/dashboard');

    refuse(harness, 'https://login.microsoftonline.test/common/discovery', 403);

    expect(harness.error).not.toHaveBeenCalled();
  });

  it('ignores every other failure status', () => {
    const harness = configure('/reports/bookkeeping');

    refuse(harness, '/api/reports/bookkeeping', 401);
    refuse(harness, '/api/reports/bookkeeping', 404);
    refuse(harness, '/api/reports/bookkeeping', 500);

    expect(harness.error).not.toHaveBeenCalled();
  });

  /**
   * AC (issue #554): "a support-session refusal is reported by `supportSessionMessageInterceptor`
   * alone". Both support-session codes are member-endpoint `403`s and would otherwise read as
   * "you don't have access to this" here too.
   */
  it.each(['read_only_support_session', 'unavailable_in_support_view'])(
    'never shows the generic member message for a support-session refusal coded %s',
    (code) => {
      const harness = configure('/reports/bookkeeping');

      refuseWithCode(harness, '/api/reports/bookkeeping', code);

      expect(harness.error).not.toHaveBeenCalled();
    }
  );

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

  it('matches the business application form (issue #543)', () => {
    expect(isAccountStateBypassRoute('/apply')).toBe(true);
  });

  it('matches any invite token by prefix', () => {
    expect(isAccountStateBypassRoute('/invite/abc123')).toBe(true);
    expect(isAccountStateBypassRoute('/invite/abc123?x=1')).toBe(true);
  });

  it('matches the tokenless /invite page the invite route leaves behind (issue #681)', () => {
    expect(isAccountStateBypassRoute('/invite')).toBe(true);
    expect(isAccountStateBypassRoute('/invitex')).toBe(false);
  });

  it('does not match an ordinary business route', () => {
    expect(isAccountStateBypassRoute('/')).toBe(false);
    expect(isAccountStateBypassRoute('/reports/bookkeeping')).toBe(false);
    expect(isAccountStateBypassRoute('/access-not-allowed')).toBe(false);
  });
});
