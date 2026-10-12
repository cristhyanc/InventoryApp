/**
 * What a refused request is, as far as the two member caches and the global `403` handler are
 * concerned (issue #602). One classification, read from the configured API base URL, so the three
 * `403` interceptors cannot disagree about what a member endpoint is - which is how the refresh
 * loop between them arose in the first place.
 */

/**
 * The kinds of request the `403` interceptors distinguish.
 *
 * - `member` - an endpoint of the current business's API, whose `403` says something about this
 *   member's role or account state. The only kind that drives a cache refresh or a message.
 * - `access-probe` - `GET /api/me/access`, the capability cache's own read (issue #521).
 * - `account-state-probe` - `GET /api/me/account-state`, the account-state cache's own read
 *   (issue #523). Membership-exempt, so its refusal says nothing about a membership at all.
 * - `platform-admin` - the platform diagnostics surface (`/api/admin/diagnostics/*`, issue #336),
 *   authorized by the configured Entra `(tid, oid)` pair and by no business role. A `403` is its
 *   expected answer for every ordinary member.
 * - `non-api` - a static asset, another origin, or anything else outside the configured API base.
 */
export type MemberRequestKind = 'member' | 'access-probe' | 'account-state-probe' | 'platform-admin' | 'non-api';

/** `GET /api/me/access`, relative to the configured API base URL. */
const ACCESS_PROBE_PATH = 'me/access';

/** `GET /api/me/account-state`, relative to the configured API base URL. */
const ACCOUNT_STATE_PROBE_PATH = 'me/account-state';

/**
 * The platform diagnostics surface, relative to the configured API base URL: `access` and `query`
 * today (issue #336). Matched as a prefix rather than endpoint by endpoint, because every endpoint
 * under it is platform-admin authorized by construction - `PlatformAdminPolicy` together with
 * `PlatformDiagnosticsEndpointAttribute`. The other `/api/admin/*` controllers are ordinary
 * capability-policy member endpoints and are deliberately not covered.
 */
const PLATFORM_ADMIN_PREFIX = 'admin/diagnostics';

/** Static and runtime-configuration assets (`/assets/config.json`), which are not API calls. */
const ASSETS_SEGMENT = 'assets';

/**
 * The document's own URL, the base a relative request URL resolves against - the same resolution
 * the browser performs when `HttpClient` issues it.
 */
function documentBase(): string {
  return typeof document === 'undefined' ? 'http://localhost/' : document.baseURI;
}

/** `url` as an absolute URL, or `null` when it is not one this build can classify. */
function absolute(url: string): URL | null {
  try {
    return new URL(url, documentBase());
  } catch {
    return null;
  }
}

/**
 * The path's non-empty segments, lower-cased.
 *
 * Splitting into segments is what makes a trailing slash, a doubled slash and a query string
 * irrelevant: `/api/me/access`, `/api/me/access/` and `/api/me/access?x=1` all yield the same
 * three segments, so none of them can defeat an exclusion. Lower-casing matches the API's own
 * case-insensitive routing, so `/api/Me/Access` reaches the capability read and is classified as
 * it; the comparisons here are only ever against fixed endpoint names, never against an
 * identifier, so nothing else is affected.
 */
function segmentsOf(path: string): string[] {
  return path
    .split('/')
    .filter((segment) => segment.length > 0)
    .map((segment) => segment.toLowerCase());
}

/**
 * How a request to `url` should be treated by the `403` interceptors, given the API base URL
 * `ConfigService` resolved (`/api` locally, the deployed API origin and path otherwise).
 *
 * **Everything is classified from the configured base, explicitly.** A request counts as an API
 * call only when its origin is the API's own and its path starts with the API's path segments, so
 * a static asset, an MSAL or other third-party origin, and a path that merely begins with the same
 * letters (`/apiary/...`) are all `non-api` and drive nothing. Within the API, the two cache
 * probes and the platform diagnostics surface are named rather than inferred, and every other
 * endpoint is a `member` one - the default has to stay that way round, because an endpoint this
 * build has never heard of still refuses a member whose role changed.
 */
export function classifyMemberRequest(url: string, apiBaseUrl: string): MemberRequestKind {
  const request = absolute(url);
  const apiBase = absolute(apiBaseUrl);
  if (request === null || apiBase === null || request.origin !== apiBase.origin) {
    return 'non-api';
  }

  const requestSegments = segmentsOf(request.pathname);
  if (requestSegments[0] === ASSETS_SEGMENT) {
    return 'non-api';
  }

  const baseSegments = segmentsOf(apiBase.pathname);
  const underApiBase =
    requestSegments.length > baseSegments.length &&
    baseSegments.every((segment, index) => segment === requestSegments[index]);
  if (!underApiBase) {
    return 'non-api';
  }

  const endpoint = requestSegments.slice(baseSegments.length);
  if (endpoint[0] === ASSETS_SEGMENT) {
    return 'non-api';
  }

  const endpointPath = endpoint.join('/');
  if (endpointPath === ACCESS_PROBE_PATH) {
    return 'access-probe';
  }
  if (endpointPath === ACCOUNT_STATE_PROBE_PATH) {
    return 'account-state-probe';
  }
  if (endpointPath === PLATFORM_ADMIN_PREFIX || endpointPath.startsWith(`${PLATFORM_ADMIN_PREFIX}/`)) {
    return 'platform-admin';
  }

  return 'member';
}
