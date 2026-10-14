import { classifyMemberRequest } from './member-endpoint';

/** The two shapes `ConfigService.apiBaseUrl` actually takes: the dev proxy path, and a deployed API. */
const LOCAL = '/api';
const DEPLOYED = 'https://api.example.test/api';

/**
 * The one request classification the three `403` interceptors read (issue #602). Each of them used
 * to carry its own `endsWith`/`includes` test, which is how `/api/admin/diagnostics/access` came to
 * look like a member refusal and how a query string could defeat an exclusion.
 */
describe('classifyMemberRequest (issue #602)', () => {
  describe('with the local dev-proxy API base URL', () => {
    it.each([
      '/api/reports/bookkeeping',
      '/api/dashboard/summary',
      '/api/me',
      '/api/suppliers/7/gst-defaults',
      // Every other /api/admin controller is an ordinary capability-policy endpoint.
      '/api/admin/historical-gst-classification/preview',
      '/api/admin/inventory-cost-repair',
      '/api/admin/nayax-sale-timestamp-repair/preview'
    ])('classifies %s as a member endpoint', (url) => {
      expect(classifyMemberRequest(url, LOCAL)).toBe('member');
    });

    it.each(['/api/me/access', '/api/me/access/', '/api/me/access?refresh=1', '/api/me/access#x', '/api/Me/Access'])(
      'classifies %s as the capability probe',
      (url) => {
        expect(classifyMemberRequest(url, LOCAL)).toBe('access-probe');
      }
    );

    it.each([
      '/api/me/account-state',
      '/api/me/account-state/',
      '/api/me/account-state?reason=refresh',
      '/api//me/account-state'
    ])('classifies %s as the account-state probe', (url) => {
      expect(classifyMemberRequest(url, LOCAL)).toBe('account-state-probe');
    });

    it.each([
      '/api/admin/diagnostics/access',
      '/api/admin/diagnostics/access/',
      '/api/admin/diagnostics/access?x=1',
      '/api/admin/diagnostics/query',
      '/api/admin/diagnostics'
    ])('classifies %s as the platform-admin surface', (url) => {
      expect(classifyMemberRequest(url, LOCAL)).toBe('platform-admin');
    });

    it.each([
      '/api/super-admin/support-sessions/7',
      '/api/super-admin/support-sessions/active',
      '/api/super-admin/support-sessions/active/end',
      '/api/super-admin/support-sessions'
    ])('classifies %s as the platform-admin surface (issue #553)', (url) => {
      expect(classifyMemberRequest(url, LOCAL)).toBe('platform-admin');
    });

    it('classifies the Super Admin businesses API as an ordinary member endpoint', () => {
      // Unlike the support-session control surface, this one is only ever called from behind
      // `platformAdminGuard`'s routed page, so a refusal reaching it at all is not the expected
      // case the exclusion above exists for.
      expect(classifyMemberRequest('/api/super-admin/businesses', LOCAL)).toBe('member');
    });

    it.each([
      '/assets/config.json',
      '/assets/config.json?v=2',
      // Another origin: MSAL's own traffic, and an API host this build was not configured with.
      'https://login.microsoftonline.test/common/discovery',
      'https://api.example.test/api/reports/bookkeeping',
      // Outside the API base: the application's own routes, and a path that merely starts the same.
      '/reports/bookkeeping',
      '/apiary/honey',
      // The base itself is not an endpoint.
      '/api',
      '/api/'
    ])('classifies %s as outside the API', (url) => {
      expect(classifyMemberRequest(url, LOCAL)).toBe('non-api');
    });
  });

  describe('with a deployed API base URL', () => {
    it('classifies an endpoint on the configured origin', () => {
      expect(classifyMemberRequest(`${DEPLOYED}/reports/bookkeeping`, DEPLOYED)).toBe('member');
    });

    it.each([`${DEPLOYED}/me/access`, `${DEPLOYED}/me/access?x=1`])('classifies %s as the capability probe', (url) => {
      expect(classifyMemberRequest(url, DEPLOYED)).toBe('access-probe');
    });

    it('classifies the account-state probe when the configured base carries a trailing slash', () => {
      expect(classifyMemberRequest(`${DEPLOYED}/me/account-state`, `${DEPLOYED}/`)).toBe('account-state-probe');
    });

    it('classifies the platform diagnostics probe', () => {
      expect(classifyMemberRequest(`${DEPLOYED}/admin/diagnostics/access`, DEPLOYED)).toBe('platform-admin');
    });

    it.each([
      // The application's own assets are served from the application origin, not the API's.
      '/assets/config.json',
      'https://api.example.test/assets/config.json',
      // A same-path endpoint on a different host is not this API.
      'https://api.other.test/api/me/access',
      // The deployed API path prefix still has to match.
      'https://api.example.test/health'
    ])('classifies %s as outside the API', (url) => {
      expect(classifyMemberRequest(url, DEPLOYED)).toBe('non-api');
    });
  });

  it('classifies an unusable URL as outside the API rather than as a member endpoint', () => {
    expect(classifyMemberRequest('http://', LOCAL)).toBe('non-api');
    expect(classifyMemberRequest('/api/reports/bookkeeping', 'http://')).toBe('non-api');
  });
});
