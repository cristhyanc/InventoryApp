import * as assert from 'assert';
import { buildProtectedResourceMap, inventoryApiScope, loginRequest } from './auth-config';
import { LOCAL_API_BASE_URL, isLocalDevelopmentHost, resolveApiBaseUrl } from './api-base-url';

/**
 * This repository has no configured Angular/Jasmine/Jest test runner (see
 * AGENTS.md and CLAUDE.md), so this file is not wired into any npm script
 * and is not part of `bash scripts/validate.sh`. It is a deterministic,
 * framework-free verification of the issue #38 authentication contract that
 * does not require bootstrapping Angular or MSAL, using Node's built-in
 * `assert`. Run it manually from the repository root with the `jiti` runner
 * that is already present in the frontend's dependency tree:
 *
 *   npm --prefix frontend/inventory-app ci
 *   npm --prefix frontend/inventory-app exec -- jiti \
 *     frontend/inventory-app/src/app/auth-config.spec.ts
 *
 * It prints one `PASS:` line per check and exits non-zero on the first failure.
 */

function check(name: string, run: () => void): void {
  run();
  // eslint-disable-next-line no-console
  console.log(`PASS: ${name}`);
}

check('loginRequest requests the delegated access_as_user API scope', () => {
  assert.deepStrictEqual(loginRequest.scopes, [inventoryApiScope]);
  assert.ok(inventoryApiScope.endsWith('/access_as_user'));
});

check('buildProtectedResourceMap matches the local dev proxy base URL', () => {
  const map = buildProtectedResourceMap('/api');

  assert.deepStrictEqual([...map.keys()], ['/api/*']);
  assert.deepStrictEqual(map.get('/api/*'), loginRequest.scopes);
});

check('buildProtectedResourceMap matches an absolute deployed API base URL', () => {
  const deployedApiBaseUrl = 'https://vm-manager-axhadxh5hjehayh0.australiaeast-01.azurewebsites.net/api';
  const map = buildProtectedResourceMap(deployedApiBaseUrl);

  assert.deepStrictEqual([...map.keys()], [`${deployedApiBaseUrl}/*`]);
  assert.deepStrictEqual(map.get(`${deployedApiBaseUrl}/*`), loginRequest.scopes);
});

check('local development resolves the API base URL to the dev proxy', () => {
  const deployedConfig = { apiBaseUrl: 'https://example-api.azurewebsites.net/api' };

  for (const hostname of ['localhost', '127.0.0.1', 'LOCALHOST']) {
    assert.ok(isLocalDevelopmentHost(hostname));
    // The tracked config.json holds the deployed URL and is still ignored locally, so no
    // developer has to edit it to switch between local work and a deployment.
    assert.strictEqual(resolveApiBaseUrl(hostname, deployedConfig), LOCAL_API_BASE_URL);
  }
});

check('a deployed host resolves the API base URL from the runtime configuration', () => {
  const deployedApiBaseUrl = 'https://example-api.azurewebsites.net/api';

  assert.ok(!isLocalDevelopmentHost('inventory.example.com'));
  assert.strictEqual(
    resolveApiBaseUrl('inventory.example.com', { apiBaseUrl: deployedApiBaseUrl }),
    deployedApiBaseUrl);
  assert.strictEqual(
    resolveApiBaseUrl('inventory.example.com', { apiBaseUrl: `${deployedApiBaseUrl}/` }),
    deployedApiBaseUrl);
});

check('a deployed host falls back to the proxy path without usable configuration', () => {
  assert.strictEqual(resolveApiBaseUrl('inventory.example.com', null), LOCAL_API_BASE_URL);
  assert.strictEqual(resolveApiBaseUrl('inventory.example.com', { apiBaseUrl: '  ' }), LOCAL_API_BASE_URL);
});

check('the protected-resource map is keyed by the resolved API base URL', () => {
  const deployedApiBaseUrl = 'https://example-api.azurewebsites.net/api';

  for (const [hostname, config] of [
    ['localhost', { apiBaseUrl: deployedApiBaseUrl }],
    ['inventory.example.com', { apiBaseUrl: deployedApiBaseUrl }]
  ] as const) {
    const resolved = resolveApiBaseUrl(hostname, config);
    const map = buildProtectedResourceMap(resolved);

    assert.deepStrictEqual([...map.keys()], [`${resolved}/*`]);
    assert.deepStrictEqual(map.get(`${resolved}/*`), loginRequest.scopes);
  }
});

// eslint-disable-next-line no-console
console.log('All auth-config checks passed.');
