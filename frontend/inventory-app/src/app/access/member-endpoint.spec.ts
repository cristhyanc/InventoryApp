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

    /**
     * The Super Admin businesses collection and every one of its subresources (issue #673). It is
     * the same class-2 platform-admin authorization as the two surfaces above, so a `403` is the
     * expected answer for every ordinary member and for an administrator whose grant has gone -
     * and the sidebar's pending-count badge reads one of these on its own, outside any page.
     */
    it.each([
      '/api/super-admin/businesses',
      '/api/super-admin/businesses/',
      '/api/super-admin/businesses/pending-count',
      '/api/super-admin/businesses/pending-count?at=now',
      '/api/super-admin/businesses/pending-count#x',
      '/api/Super-Admin/Businesses/Pending-Count',
      '/api//super-admin/businesses/pending-count',
      '/api/super-admin/businesses/7/approve',
      '/api/super-admin/businesses/7/reject',
      '/api/super-admin/businesses/7/deactivate',
      '/api/super-admin/businesses/7/reactivate',
      '/api/super-admin/businesses/7/members',
      '/api/super-admin/businesses/7/members/19/release'
    ])('classifies %s as the platform-admin surface (issue #673)', (url) => {
      expect(classifyMemberRequest(url, LOCAL)).toBe('platform-admin');
    });

    /**
     * The platform supplier presets of issue #848: the collection and `{id}`. Class-2
     * platform-admin authorization again, and this one names no business at all, so a `403` from it
     * says nothing about a membership or a business role either.
     */
    it.each([
      '/api/super-admin/supplier-presets',
      '/api/super-admin/supplier-presets/',
      '/api/super-admin/supplier-presets/7',
      '/api/Super-Admin/Supplier-Presets/7',
      '/api/super-admin/supplier-presets?x=1'
    ])('classifies %s as the platform-admin surface (issue #848)', (url) => {
      expect(classifyMemberRequest(url, LOCAL)).toBe('platform-admin');
    });

    /**
     * The shared price catalogue's review of issue #928: the queue, a listing's decision, a waiting
     * product's match and an automatic match's confirm or undo. Class-2 platform-admin authorization
     * again, and it names no business either - a waiting product is a search name, a unit label and a
     * count - so a `403` from it says nothing about a membership or a business role.
     */
    it.each([
      '/api/super-admin/price-catalogue-review',
      '/api/super-admin/price-catalogue-review/',
      '/api/super-admin/price-catalogue-review/listings/7/decision',
      '/api/super-admin/price-catalogue-review/matches',
      '/api/super-admin/price-catalogue-review/automatic-matches/7/confirm',
      '/api/super-admin/price-catalogue-review/automatic-matches/7/undo',
      '/api/Super-Admin/Price-Catalogue-Review/matches'
    ])('classifies %s as the platform-admin surface (issue #928)', (url) => {
      expect(classifyMemberRequest(url, LOCAL)).toBe('platform-admin');
    });

    /**
     * The shared catalogue's own master products of issue #1023: the searchable, paged listing and one
     * product with its supplier listings. Class-2 platform-admin authorization again, and both are
     * reads of platform data that names no business - so a `403` from either says nothing about a
     * membership or a business role, and neither may carry a role-preview or support-session header.
     */
    it.each([
      '/api/super-admin/master-products',
      '/api/super-admin/master-products/',
      '/api/super-admin/master-products?search=cola&page=2',
      '/api/super-admin/master-products/7',
      '/api/Super-Admin/Master-Products/7'
    ])('classifies %s as the platform-admin surface (issue #1023)', (url) => {
      expect(classifyMemberRequest(url, LOCAL)).toBe('platform-admin');
    });

    it.each([
      // A segment that merely begins with the same letters is a different endpoint, and the
      // default for anything the exclusions do not name has to stay `member`.
      '/api/super-admin/businesses-archive',
      '/api/super-admin-businesses/pending-count',
      '/api/super-admin/supplier-presets-archive',
      '/api/super-admin/price-catalogue-review-archive',
      '/api/super-admin/master-products-archive',
      // The business's own suppliers are an ordinary member endpoint and must stay one.
      '/api/suppliers',
      '/api/suppliers/7'
    ])('classifies %s as a member endpoint', (url) => {
      expect(classifyMemberRequest(url, LOCAL)).toBe('member');
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
      `${DEPLOYED}/super-admin/businesses`,
      `${DEPLOYED}/super-admin/businesses/pending-count`,
      `${DEPLOYED}/super-admin/businesses/pending-count?x=1`,
      `${DEPLOYED}/super-admin/businesses/7/members/19/release`
    ])('classifies %s as the platform-admin surface (issue #673)', (url) => {
      expect(classifyMemberRequest(url, DEPLOYED)).toBe('platform-admin');
    });

    it('classifies the pending-count read when the configured base carries a trailing slash', () => {
      expect(classifyMemberRequest(`${DEPLOYED}/super-admin/businesses/pending-count`, `${DEPLOYED}/`)).toBe(
        'platform-admin'
      );
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
