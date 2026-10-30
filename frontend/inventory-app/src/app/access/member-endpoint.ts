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
 *   the Super Admin businesses surface (`/api/super-admin/businesses/*`, issues #544 and #545) and
 *   the Super Admin support-session control surface (`/api/super-admin/support-sessions/*`, issue
 *   #550), all authorized by the configured Entra `(tid, oid)` pair and by no business role. A
 *   `403` is their expected answer for every ordinary member.
 * - `non-api` - a static asset, another origin, or anything else outside the configured API base.
 */
export type MemberRequestKind = 'member' | 'access-probe' | 'account-state-probe' | 'platform-admin' | 'non-api';

/** `GET /api/me/access`, relative to the configured API base URL. */
const ACCESS_PROBE_PATH = 'me/access';

/** `GET /api/me/account-state`, relative to the configured API base URL. */
const ACCOUNT_STATE_PROBE_PATH = 'me/account-state';

/**
 * The platform-admin surfaces, relative to the configured API base URL. Each is matched as a
 * prefix rather than endpoint by endpoint, because every endpoint under one of them is
 * platform-admin authorized by construction - `PlatformAdminPolicy` together with
 * `PlatformDiagnosticsEndpointAttribute`, class 2 of the four endpoint authorization classes
 * (docs/architecture.md § Capability authorization) - and so refuses every ordinary member and
 * every administrator whose configured grant has gone:
 *
 * - `admin/diagnostics` - the platform diagnostics surface, `access` and `query` today (issue
 *   #336). The other `/api/admin/*` controllers are ordinary capability-policy member endpoints and
 *   are deliberately not covered.
 * - `super-admin/businesses` - the Super Admin businesses collection and every one of its
 *   subresources (issues #544 and #545, and issue #673 for this classification): the directory
 *   itself, `pending-count`, `{businessId}/approve`, `.../reject`, `.../deactivate`,
 *   `.../reactivate`, `.../members` and `.../members/{membershipId}/release`. The sidebar's
 *   pending-count badge reads one of these on its own, with no page behind it, so a refusal of it
 *   must drive nothing: refreshing the member capability cache from one made every capability
 *   emission ask the badge for the count again.
 * - `super-admin/support-sessions` - the control surface of issue #550: `{businessId}`, `active`
 *   and `active/end`, reached as the administrator themselves and never inside a session, so a
 *   refusal here says nothing about a business role either. Issue #553's own calls to it (the "view
 *   as this business" dialog, the banner's end action, and a tab's own-session probe) must not be
 *   read as a member refusal any more than the diagnostics probe is.
 * - `super-admin/supplier-presets` - the platform supplier presets of issue #848: the collection and
 *   `{id}`. It names no business at all, so a `403` from it says nothing about a membership or a
 *   business role, and it must not carry the role-preview or support-session header any more than
 *   the three above do.
 * - `super-admin/price-catalogue-review` - the shared price catalogue's review of issue #928: the
 *   queue, a listing's decision, a waiting product's match and an automatic match's confirm or undo.
 *   It names no business either - a waiting product is a search name, a unit label and a count - so a
 *   `403` from it says nothing about a membership or a business role.
 */
const PLATFORM_ADMIN_PREFIXES = [
  'admin/diagnostics',
  'super-admin/businesses',
  'super-admin/support-sessions',
  'super-admin/supplier-presets',
  'super-admin/price-catalogue-review'
];

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
 * probes and each {@link PLATFORM_ADMIN_PREFIXES} surface are named rather than inferred, and every
 * other endpoint is a `member` one - the default has to stay that way round, because an endpoint
 * this build has never heard of still refuses a member whose role changed.
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
  if (PLATFORM_ADMIN_PREFIXES.some((prefix) => endpointPath === prefix || endpointPath.startsWith(`${prefix}/`))) {
    return 'platform-admin';
  }

  return 'member';
}
