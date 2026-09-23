import {
  BrowserCacheLocation,
  Configuration
} from '@azure/msal-browser';

const SPA_CLIENT_ID = '0d451a7e-c66b-4550-be18-c36dc2753baf';
const API_CLIENT_ID = '3dd61784-7289-4c2d-b0aa-3bb586a92004';

export const inventoryApiScope =
  `api://${API_CLIENT_ID}/access_as_user`;

// A function rather than a module-level constant so this file has no top-level `window`
// access: it stays importable from a plain Node test runner (see auth-config.spec.ts).
export function createMsalConfig(): Configuration {
  return {
    auth: {
      clientId: SPA_CLIENT_ID,

      // Multitenant: allow Microsoft Entra users from different organisations.
      authority: 'https://login.microsoftonline.com/common',

      redirectUri: `${window.location.origin}/auth`,

      postLogoutRedirectUri: window.location.origin
    },

    cache: {
      cacheLocation: BrowserCacheLocation.LocalStorage
    }
  };
}

export const loginRequest = {
  scopes: [
    inventoryApiScope
  ]
};

/**
 * MSAL matches a protectedResourceMap key against the request URL after resolving it
 * relative to the current origin, so a relative apiBaseUrl (the local dev proxy, "/api")
 * and an absolute one (the deployed Azure API) both work: the bearer token attaches to
 * the API this build/environment is actually configured to call (see ConfigService).
 */
export function buildProtectedResourceMap(apiBaseUrl: string): Map<string, Array<string>> {
  const protectedResourceMap = new Map<string, Array<string>>();

  protectedResourceMap.set(`${apiBaseUrl}/*`, loginRequest.scopes);

  return protectedResourceMap;
}
