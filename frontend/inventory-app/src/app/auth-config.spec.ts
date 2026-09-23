import * as assert from 'assert';
import { buildProtectedResourceMap, inventoryApiScope, loginRequest } from './auth-config';

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

// eslint-disable-next-line no-console
console.log('All auth-config checks passed.');
