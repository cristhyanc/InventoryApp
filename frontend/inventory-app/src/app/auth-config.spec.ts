import {
  apiScopeFor,
  buildProtectedResourceMap,
  clientIdFor,
  createMsalConfig,
  inventoryApiScope,
  isExternalIdConfigured,
  loginRequest,
  loginRequestFor
} from './auth-config';
import { LOCAL_API_BASE_URL, isLocalDevelopmentHost, resolveApiBaseUrl } from './api-base-url';
import {
  CONFIGURED_EXTERNAL_ID,
  configureExternalId,
  unconfigureExternalId
} from './auth/external-id.spec-helpers';

describe('auth-config', () => {
  afterEach(() => unconfigureExternalId());

  // Issue #710 added a second trusted issuer to the API; issue #711 added the SPA's side of it, as
  // a second authority with its own client id, its own API audience and therefore its own MSAL
  // cache partition. **With no customer identity tenant configured - the shipped state - there is
  // still exactly one authority and exactly one audience**, which is what keeps "no stale or
  // wrong-provider token can be sent or selected" true for every deployment that has not been
  // configured by a human.
  it('signs in against exactly one authority and asks for exactly one API audience', () => {
    const { auth } = createMsalConfig();

    expect(auth.authority).toBe('https://login.microsoftonline.com/common');
    expect(loginRequest.scopes).toHaveLength(1);
    expect(inventoryApiScope).toMatch(/^api:\/\/[0-9a-f-]+\/access_as_user$/);
    expect(isExternalIdConfigured()).toBe(false);
  });

  it('attaches a token to the API alone, so an unrelated destination matches no entry', () => {
    const apiBaseUrl = 'https://example-api.azurewebsites.net/api';
    const map = buildProtectedResourceMap(apiBaseUrl);

    expect([...map.keys()]).toEqual([`${apiBaseUrl}/*`]);

    // Nothing else is protected, so MSAL has no entry to attach a bearer token to for the
    // application's own origin, the runtime configuration file it loads before the interceptor
    // exists, or any Microsoft API.
    for (const unrelated of [
      'https://graph.microsoft.com/v1.0/me',
      'https://example.com/assets/config.json',
      'assets/config.json',
      'https://example-api.azurewebsites.net/health'
    ]) {
      expect([...map.keys()].some((key) => unrelated.startsWith(key.replace(/\*$/, '')))).toBe(false);
    }
  });

  it('loginRequest requests the delegated access_as_user API scope', () => {
    expect(loginRequest.scopes).toEqual([inventoryApiScope]);
    expect(inventoryApiScope.endsWith('/access_as_user')).toBe(true);
  });

  it('buildProtectedResourceMap matches the local dev proxy base URL', () => {
    const map = buildProtectedResourceMap('/api');

    expect([...map.keys()]).toEqual(['/api/*']);
    expect(map.get('/api/*')).toEqual(loginRequest.scopes);
  });

  it('buildProtectedResourceMap matches an absolute deployed API base URL', () => {
    const deployedApiBaseUrl = 'https://vm-manager-axhadxh5hjehayh0.australiaeast-01.azurewebsites.net/api';
    const map = buildProtectedResourceMap(deployedApiBaseUrl);

    expect([...map.keys()]).toEqual([`${deployedApiBaseUrl}/*`]);
    expect(map.get(`${deployedApiBaseUrl}/*`)).toEqual(loginRequest.scopes);
  });

  it('local development resolves the API base URL to the dev proxy', () => {
    const deployedConfig = { apiBaseUrl: 'https://example-api.azurewebsites.net/api' };

    for (const hostname of ['localhost', '127.0.0.1', 'LOCALHOST']) {
      expect(isLocalDevelopmentHost(hostname)).toBe(true);
      // The tracked config.json holds the deployed URL and is still ignored locally, so no
      // developer has to edit it to switch between local work and a deployment.
      expect(resolveApiBaseUrl(hostname, deployedConfig)).toBe(LOCAL_API_BASE_URL);
    }
  });

  it('a deployed host resolves the API base URL from the runtime configuration', () => {
    const deployedApiBaseUrl = 'https://example-api.azurewebsites.net/api';

    expect(isLocalDevelopmentHost('inventory.example.com')).toBe(false);
    expect(resolveApiBaseUrl('inventory.example.com', { apiBaseUrl: deployedApiBaseUrl })).toBe(deployedApiBaseUrl);
    expect(resolveApiBaseUrl('inventory.example.com', { apiBaseUrl: `${deployedApiBaseUrl}/` })).toBe(deployedApiBaseUrl);
  });

  it('a deployed host falls back to the proxy path without usable configuration', () => {
    expect(resolveApiBaseUrl('inventory.example.com', null)).toBe(LOCAL_API_BASE_URL);
    expect(resolveApiBaseUrl('inventory.example.com', { apiBaseUrl: '  ' })).toBe(LOCAL_API_BASE_URL);
  });

  it('the protected-resource map is keyed by the resolved API base URL', () => {
    const deployedApiBaseUrl = 'https://example-api.azurewebsites.net/api';

    for (const [hostname, config] of [
      ['localhost', { apiBaseUrl: deployedApiBaseUrl }],
      ['inventory.example.com', { apiBaseUrl: deployedApiBaseUrl }]
    ] as const) {
      const resolved = resolveApiBaseUrl(hostname, config);
      const map = buildProtectedResourceMap(resolved);

      expect([...map.keys()]).toEqual([`${resolved}/*`]);
      expect(map.get(`${resolved}/*`)).toEqual(loginRequest.scopes);
    }
  });

  /**
   * The second authority (issue #711). Everything here is off until a human fills in the four
   * identifiers, and each half-filled or malformed combination means "not configured" rather than
   * "configured badly" - a configuration mistake must never produce a sign-in page offering a
   * journey that cannot complete.
   */
  describe('the customer identity authority', () => {
    it('is not configured while any identifier is missing or not a usable https authority', () => {
      for (const broken of [
        { instance: '' },
        { tenantId: '  ' },
        { clientId: '' },
        { apiClientId: '' },
        { instance: 'contoso.ciamlogin.com' },
        { instance: 'http://contoso.ciamlogin.com/' }
      ]) {
        configureExternalId(broken);
        expect(isExternalIdConfigured()).toBe(false);
        expect(() => createMsalConfig('externalId')).toThrow();
      }
    });

    it('builds one pinned authority, its own client id and its own known host', () => {
      configureExternalId();

      const { auth, cache } = createMsalConfig('externalId');

      expect(isExternalIdConfigured()).toBe(true);
      expect(auth.authority).toBe(`https://contoso.ciamlogin.com/${CONFIGURED_EXTERNAL_ID.tenantId}/`);
      expect(auth.knownAuthorities).toEqual(['contoso.ciamlogin.com']);
      expect(auth.clientId).toBe(CONFIGURED_EXTERNAL_ID.clientId);
      expect(auth.redirectUri).toBe(`${window.location.origin}/auth`);
      expect(auth.postLogoutRedirectUri).toBe(window.location.origin);
      expect(cache?.cacheLocation).toBe('localStorage');
    });

    /**
     * MSAL partitions its cache by client id and authority, so two authorities must never share
     * either. Nothing here may be derived from the workforce registration.
     */
    it('shares neither its client id nor its API audience with the workforce authority', () => {
      configureExternalId();

      expect(clientIdFor('externalId')).not.toBe(clientIdFor('workforce'));
      expect(apiScopeFor('externalId')).not.toBe(apiScopeFor('workforce'));
      expect(apiScopeFor('externalId')).toBe(`api://${CONFIGURED_EXTERNAL_ID.apiClientId}/access_as_user`);
      expect(createMsalConfig('externalId').auth.authority).not.toBe(createMsalConfig().auth.authority);
    });

    /**
     * `oid` and `tid` are issued to an application only when the token request includes the
     * `profile` scope (design § 4 and § 13 item 3), and they are the entire identity this API
     * reads. MSAL Browser adds it to an interactive request by default; the design says not to
     * rely on that silently, so the request names it and this test is the proof.
     */
    it('asks for the profile scope explicitly, beside its own API audience', () => {
      configureExternalId();

      const { scopes } = loginRequestFor('externalId');

      expect(scopes).toContain('profile');
      expect(scopes).toContain('openid');
      expect(scopes).toContain(apiScopeFor('externalId'));
      expect(scopes).not.toContain(inventoryApiScope);
    });

    it('attaches its own audience to the API, never the workforce one', () => {
      configureExternalId();

      const map = buildProtectedResourceMap('/api', 'externalId');

      expect(map.get('/api/*')).toEqual([apiScopeFor('externalId')]);
      expect(map.get('/api/*')).not.toContain(inventoryApiScope);
    });
  });
});
