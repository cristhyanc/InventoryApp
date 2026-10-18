import { buildProtectedResourceMap, createMsalConfig, inventoryApiScope, loginRequest } from './auth-config';
import { LOCAL_API_BASE_URL, isLocalDevelopmentHost, resolveApiBaseUrl } from './api-base-url';

describe('auth-config', () => {
  // Issue #710 adds a second trusted issuer to the API only. The SPA keeps exactly one authority
  // and one API audience, and the authority selection, the second MSAL instance and its own cache
  // partition are issues #711/#712 (design § 16). These two assertions are what makes "no stale or
  // wrong-provider token can be sent or selected" true today: there is one authority to acquire a
  // token from, one audience to acquire it for, and one destination it is ever attached to.
  it('signs in against exactly one authority and asks for exactly one API audience', () => {
    const { auth } = createMsalConfig();

    expect(auth.authority).toBe('https://login.microsoftonline.com/common');
    expect(loginRequest.scopes).toHaveLength(1);
    expect(inventoryApiScope).toMatch(/^api:\/\/[0-9a-f-]+\/access_as_user$/);
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
});
