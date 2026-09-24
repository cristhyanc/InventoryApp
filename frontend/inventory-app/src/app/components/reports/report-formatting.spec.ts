import * as assert from 'assert';
import { money, moneyOrUnavailable, percentOrUnavailable } from './report-formatting';

/**
 * This repository has no configured Angular/Jasmine/Jest test runner (see
 * AGENTS.md and CLAUDE.md), so this file is not wired into any npm script
 * and is not part of `bash scripts/validate.sh`. It is a deterministic,
 * framework-free verification of the issue #58 null-vs-zero financial
 * display contract, using Node's built-in `assert`. Run it manually from
 * the repository root with the `jiti` runner that is already present in
 * the frontend's dependency tree:
 *
 *   npm --prefix frontend/inventory-app ci
 *   npm --prefix frontend/inventory-app exec -- jiti \
 *     frontend/inventory-app/src/app/components/reports/report-formatting.spec.ts
 *
 * It prints one `PASS:` line per check and exits non-zero on the first failure.
 */

function check(name: string, run: () => void): void {
  run();
  // eslint-disable-next-line no-console
  console.log(`PASS: ${name}`);
}

check('money formats a known amount as AUD currency', () => {
  assert.strictEqual(money(12.5), '$12.50');
  assert.strictEqual(money(0), '$0.00');
});

check('moneyOrUnavailable keeps a known zero distinct from an unknown value', () => {
  assert.strictEqual(moneyOrUnavailable(null), 'Profit unavailable');
  assert.strictEqual(moneyOrUnavailable(undefined), 'Profit unavailable');
  assert.strictEqual(moneyOrUnavailable(0), '$0.00');
  assert.strictEqual(moneyOrUnavailable(42.1), '$42.10');
});

check('moneyOrUnavailable accepts a caller-supplied unavailable label', () => {
  assert.strictEqual(moneyOrUnavailable(null, 'Unavailable'), 'Unavailable');
});

check('percentOrUnavailable keeps a known zero margin distinct from an unknown margin', () => {
  assert.strictEqual(percentOrUnavailable(null), '—');
  assert.strictEqual(percentOrUnavailable(undefined), '—');
  assert.strictEqual(percentOrUnavailable(0), '0.0%');
  assert.strictEqual(percentOrUnavailable(12.34), '12.3%');
});

check('percentOrUnavailable applies a suffix only when the value is known', () => {
  assert.strictEqual(percentOrUnavailable(5, ' margin'), '5.0% margin');
  assert.strictEqual(percentOrUnavailable(null, ' margin'), '—');
});

// eslint-disable-next-line no-console
console.log('All report-formatting checks passed.');
