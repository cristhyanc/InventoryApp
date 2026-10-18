import {
  BrowserCacheLocation,
  Configuration
} from '@azure/msal-browser';

const SPA_CLIENT_ID = '0d451a7e-c66b-4550-be18-c36dc2753baf';
const API_CLIENT_ID = '3dd61784-7289-4c2d-b0aa-3bb586a92004';

export const inventoryApiScope =
  `api://${API_CLIENT_ID}/access_as_user`;

/**
 * Which trusted authority a sign-in is made against (issue #711, design
 * `docs/identity-provider-decision.md` § 9.2).
 *
 * `workforce` is the Microsoft Entra ID `common` authority this application has always used, for
 * work and school accounts in any Entra tenant. `externalId` is the one configured Microsoft Entra
 * External ID tenant the API learned to trust in issue #710 - the hosted customer-identity journey,
 * which is where Google sign-in happens. The two are never pooled: each has its own client id, its
 * own authority, its own API audience and therefore its own MSAL cache partition.
 */
export type AuthorityId = 'workforce' | 'externalId';

/** The identifiers one authority's app registrations are known by. */
export interface ExternalIdSettings {
  /** The tenant's authority host, `https://<tenant-subdomain>.ciamlogin.com/`. */
  instance: string;
  /** The External ID tenant's GUID. */
  tenantId: string;
  /** The SPA app registration **in that tenant** - a different registration from the workforce one. */
  clientId: string;
  /** The API app registration in that tenant, whose `access_as_user` scope the SPA asks for. */
  apiClientId: string;
}

/**
 * The SPA half of the API's `ExternalId` configuration section, and **empty in source control for
 * the same reason it is** (design § 5.3 and § 14.3): empty is the fail-closed shipped state and the
 * rollback lever. With these empty there is no second authority at all - no Google action is
 * rendered, no second MSAL instance can be built, and the application signs in exactly as it did
 * before issue #711.
 *
 * None of these four values is a credential. The SPA is a public client using authorization code
 * with PKCE and holds no client secret; the Google OAuth client secret lives only inside the
 * External ID tenant's provider configuration. They are identifiers, the same classification the
 * workforce client ids above already have, and a human fills them in per deployment - see
 * `README.md` § Signing in with Google.
 */
export const externalIdSettings: ExternalIdSettings = {
  instance: '',
  tenantId: '',
  clientId: '',
  apiClientId: ''
};

/**
 * Whether this build has a complete, usable second authority. Half-filled means **not configured**,
 * exactly as the API treats a half-filled `ExternalId` section: a configuration mistake must never
 * become a broken sign-in page offering a journey that cannot complete.
 */
export function isExternalIdConfigured(settings: ExternalIdSettings = externalIdSettings): boolean {
  const filled = [settings.instance, settings.tenantId, settings.clientId, settings.apiClientId].every(
    (value) => value.trim().length > 0
  );

  return filled && externalIdAuthorityHost(settings) !== null;
}

/** The host MSAL must be told to trust, or `null` when the configured instance is not a usable URL. */
function externalIdAuthorityHost(settings: ExternalIdSettings): string | null {
  try {
    const instance = new URL(settings.instance.trim());
    return instance.protocol === 'https:' ? instance.host : null;
  } catch {
    return null;
  }
}

/** The delegated API scope of the authority's own API app registration. */
export function apiScopeFor(authority: AuthorityId, settings: ExternalIdSettings = externalIdSettings): string {
  return authority === 'workforce'
    ? inventoryApiScope
    : `api://${settings.apiClientId.trim()}/access_as_user`;
}

/** The client id the authority's MSAL instance is created with; also what identifies that instance. */
export function clientIdFor(authority: AuthorityId, settings: ExternalIdSettings = externalIdSettings): string {
  return authority === 'workforce' ? SPA_CLIENT_ID : settings.clientId.trim();
}

// A function rather than a module-level constant so this file has no top-level `window`
// access: it stays importable from a plain Node test runner (see auth-config.spec.ts).
export function createMsalConfig(
  authority: AuthorityId = 'workforce',
  settings: ExternalIdSettings = externalIdSettings
): Configuration {
  const shared = {
    redirectUri: `${window.location.origin}/auth`,
    postLogoutRedirectUri: window.location.origin
  };

  const cache = {
    cacheLocation: BrowserCacheLocation.LocalStorage
  };

  if (authority === 'workforce') {
    return {
      auth: {
        clientId: SPA_CLIENT_ID,

        // Multitenant: allow Microsoft Entra users from different organisations.
        authority: 'https://login.microsoftonline.com/common',

        ...shared
      },

      cache
    };
  }

  const host = externalIdAuthorityHost(settings);
  if (!isExternalIdConfigured(settings) || host === null) {
    // Reached only by a caller that ignored `isExternalIdConfigured`. Refusing is the fail-closed
    // answer: a half-built MSAL instance would redirect somewhere nobody configured.
    throw new Error('No customer identity tenant is configured for this deployment.');
  }

  let instance = settings.instance.trim();
  while (instance.endsWith('/')) {
    instance = instance.slice(0, -1);
  }

  return {
    auth: {
      clientId: settings.clientId.trim(),

      // The one configured External ID tenant, and nothing else (design § 4). `knownAuthorities`
      // is what makes MSAL trust a `ciamlogin.com` host's own metadata document rather than
      // treating it as an unknown authority.
      authority: `${instance}/${settings.tenantId.trim()}/`,
      knownAuthorities: [host],

      ...shared
    },

    cache
  };
}

/**
 * What a sign-in against `authority` asks for.
 *
 * The External ID request names `profile` **explicitly** (design § 4): `oid` and `tid` are issued
 * to an application only when the token request includes it, and they are the whole identity this
 * API reads. MSAL Browser does add `openid` and `profile` to an interactive request by default, but
 * the design says not to rely on that silently, so it is written here and asserted by a test.
 */
export function loginRequestFor(
  authority: AuthorityId,
  settings: ExternalIdSettings = externalIdSettings
): { scopes: string[] } {
  return authority === 'workforce'
    ? { scopes: [inventoryApiScope] }
    : { scopes: ['openid', 'profile', apiScopeFor('externalId', settings)] };
}

export const loginRequest = loginRequestFor('workforce');

/**
 * MSAL matches a protectedResourceMap key against the request URL after resolving it
 * relative to the current origin, so a relative apiBaseUrl (the local dev proxy, "/api")
 * and an absolute one (the deployed Azure API) both work: the bearer token attaches to
 * the API this build/environment is actually configured to call (see ConfigService).
 *
 * The scope is the **signed-in authority's own** API audience (issue #711). The two authorities
 * expose `access_as_user` from two different API app registrations, and each bearer scheme on the
 * API validates only its own audience, so asking for the other one's would acquire a token this API
 * refuses.
 */
export function buildProtectedResourceMap(
  apiBaseUrl: string,
  authority: AuthorityId = 'workforce',
  settings: ExternalIdSettings = externalIdSettings
): Map<string, Array<string>> {
  const protectedResourceMap = new Map<string, Array<string>>();

  protectedResourceMap.set(`${apiBaseUrl}/*`, [apiScopeFor(authority, settings)]);

  return protectedResourceMap;
}
