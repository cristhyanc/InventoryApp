// Unit tests for the review-result transport (scripts/agent-review-transport.mjs). The end-to-end
// hand-off through the real workflow steps is in agent-review-publication.test.mjs.
// Run with: node --test scripts/agent-review-transport.test.mjs (also imported by validate-agent-workflows.test.mjs).
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { describe, it } from 'node:test';
import {
  MAX_ENVELOPE_BYTES, TransportError, artifactName, contextFromEnv, contractFailures, packageReview, redactSecrets, redactText, verifyEnvelope,
} from './agent-review-transport.mjs';

const SHA = '6b5ade799d290fb2c4e59710cbf047c43a305df0';
const ENV = {
  GITHUB_REPOSITORY: 'owner/InventoryApp', PR_NUMBER: '267', HEAD_SHA: SHA, AGENT_MODE: 'cross-claude',
  IMPLEMENTER: 'claude', REVIEWER: 'copilot', GITHUB_RUN_ID: '38050149174', GITHUB_RUN_ATTEMPT: '1',
};
const context = contextFromEnv(ENV);
const review = (overrides = {}) => ({
  reviewed_head_sha: SHA,
  verdict: 'READY FOR HUMAN REVIEW',
  blockers: [],
  criteria: [{ criterion: 'Stock adjustments stay correct', status: 'met', evidence: 'Test InventoryCount_Conflict passes.' }],
  suggestions: [],
  validation_evidence: 'agent-validation success on the exact SHA.',
  inline_comments: [],
  ...overrides,
});
const code = (fn) => {
  try { fn(); } catch (error) { assert.ok(error instanceof TransportError, error); return error.code; }
  assert.fail('expected a TransportError');
};

// Credential-shaped fixtures are assembled at run time from obviously fake parts, so the source
// never holds a literal that a secret scanner would flag.
const b64url = (value) => Buffer.from(JSON.stringify(value)).toString('base64url');
const fakeJwt = [b64url({ alg: 'none', typ: 'fixture' }), b64url({ sub: 'fixture-only' }), 'f'.repeat(24)].join('.');
const fakeKeyBlock = ['-----BEGIN', 'RSA PRIVATE KEY-----\nfixture\n-----END', 'RSA PRIVATE KEY-----'].join(' ');
const fakeAwsKey = ['AKIA', 'FIXTURE0', 'FIXTURE0'].join('');

describe('secret redaction', () => {
  it('redacts credential-shaped values and keeps the surrounding text', () => {
    for (const secret of [
      `ghs_${'A1b2'.repeat(9)}`, `ghp_${'A1b2'.repeat(9)}`, `github_pat_${'A1b2_'.repeat(6)}`, `sk-ant-oat01-${'x'.repeat(40)}`,
      `sk-proj-${'y'.repeat(40)}`, fakeAwsKey, `xoxb-${'1'.repeat(12)}`, fakeJwt, fakeKeyBlock,
    ]) {
      const { value, count } = redactText(`before ${secret} after`);
      assert.equal(value, 'before [REDACTED] after', secret);
      assert.equal(count, 1, secret);
    }
  });

  it('keeps the key name of connection-string and header secrets', () => {
    assert.equal(redactText('Server=x;Password=hunter2;Database=y').value, 'Server=x;Password=[REDACTED];Database=y');
    assert.equal(redactText('DefaultEndpointsProtocol=https;AccountKey=abcd1234efgh5678==;').value, 'DefaultEndpointsProtocol=https;AccountKey=[REDACTED];');
    assert.equal(redactText(`Authorization: Bearer ${'t'.repeat(30)}`).value, 'Authorization: Bearer [REDACTED]');
    assert.equal(redactText('https://x-access-token:abc123@github.com/o/r').value, 'https://[REDACTED]@github.com/o/r');
  });

  it('leaves commit SHAs, paths, test names and ordinary prose untouched', () => {
    const text = `Head ${SHA} passes InventoryCount_Conflict in backend/Inventory.UnitTests/Purchases/PurchaseGstPolicyTests.cs:42; the Bearer token flow is unchanged and the password field stays hidden.`;
    assert.deepEqual(redactText(text), { value: text, count: 0 });
  });

  it('redacts every string inside a review and counts each value', () => {
    const { value, count } = redactSecrets(review({ blockers: [`key ghs_${'Q'.repeat(36)}`], inline_comments: [{ path: 'a.cs', line: 3, body: 'Password=p' }] }));
    assert.equal(count, 2);
    assert.deepEqual(value.blockers, ['key [REDACTED]']);
    assert.equal(value.inline_comments[0].line, 3);
  });
});

describe('review contract', () => {
  it('accepts a valid review and names each failed check without content', () => {
    assert.deepEqual(contractFailures(review()), []);
    assert.deepEqual(contractFailures(review({ verdict: 'APPROVE' })), ['verdict']);
    assert.deepEqual(contractFailures({ ...review(), approved: true }), ['exact_keys']);
    assert.deepEqual(contractFailures(review({ criteria: [] })), ['criteria']);
    assert.deepEqual(contractFailures(review({ inline_comments: [{ path: 'a', line: 1.5, body: 'b' }] })), ['inline_comments']);
    assert.deepEqual(contractFailures([]), ['root_is_object']);
  });
});

describe('packaging', () => {
  it('binds the review to the repository, pull request, head, route and run', () => {
    const { text, redactions } = packageReview(JSON.stringify(review()), context);
    const envelope = JSON.parse(text);
    assert.equal(redactions, 0);
    assert.deepEqual(
      { ...envelope, review: undefined },
      { schema: 'inventoryapp.agent-review-result.v1', repository: 'owner/InventoryApp', pr_number: 267, head_sha: SHA, agent_mode: 'cross-claude', implementer: 'claude', reviewer: 'copilot', run_id: 38050149174, run_attempt: 1, redactions: 0, review: undefined },
    );
    assert.equal(artifactName(context), 'agent-review-result-copilot-38050149174');
  });

  for (const [name, raw, expected] of [
    ['empty output', '', 'empty'],
    ['whitespace output', '  \n', 'empty'],
    ['invalid JSON', '{"verdict":', 'malformed'],
    ['a contract violation', JSON.stringify(review({ blockers: [''] })), 'contract'],
    ['another SHA', JSON.stringify(review({ reviewed_head_sha: 'f'.repeat(40) })), 'wrong-sha'],
    ['an oversized review', JSON.stringify(review({ validation_evidence: 'x'.repeat(MAX_ENVELOPE_BYTES) })), 'oversized'],
  ]) {
    it(`refuses ${name}`, () => assert.equal(code(() => packageReview(raw, context)), expected));
  }

  it('refuses an incomplete or unknown trusted context', () => {
    for (const [key, value] of [['HEAD_SHA', 'abc'], ['PR_NUMBER', ''], ['AGENT_MODE', 'cross-copilot'], ['REVIEWER', 'openai'], ['IMPLEMENTER', 'copilot'], ['GITHUB_RUN_ID', ''], ['GITHUB_RUN_ATTEMPT', '0']]) {
      assert.equal(code(() => contextFromEnv({ ...ENV, [key]: value })), 'context', key);
    }
  });
});

describe('verification', () => {
  const packaged = (r = review(), overrides = {}) => Buffer.from(JSON.stringify({ ...JSON.parse(packageReview(JSON.stringify(r), context).text), ...overrides }));

  it('returns the review only for a matching envelope', () => {
    assert.deepEqual(verifyEnvelope(packaged(), context), { review: review(), redactions: 0 });
  });

  it('refuses an envelope bound to anything else', () => {
    for (const overrides of [{ pr_number: 1 }, { head_sha: 'f'.repeat(40) }, { agent_mode: 'full-claude' }, { reviewer: 'claude' }, { implementer: 'copilot' }, { repository: 'a/b' }, { run_id: 1 }, { run_attempt: 2 }]) {
      assert.equal(code(() => verifyEnvelope(packaged(review(), overrides), context)), 'mismatch', JSON.stringify(overrides));
    }
    assert.equal(code(() => verifyEnvelope(Buffer.from(''), context)), 'empty');
    assert.equal(code(() => verifyEnvelope(Buffer.from('nope'), context)), 'malformed');
    assert.equal(code(() => verifyEnvelope(packaged(review(), { schema: 'x' }), context)), 'malformed');
  });
});

describe('command line', () => {
  const script = new URL('./agent-review-transport.mjs', import.meta.url).pathname;
  it('packages a file and reports the artifact name and digest, and exits 2 with a coded reason on bad input', () => {
    const dir = mkdtempSync(join(tmpdir(), 'agent-review-transport-cli-'));
    try {
      writeFileSync(join(dir, 'raw.json'), JSON.stringify(review()));
      const ok = spawnSync(process.execPath, [script, 'package', join(dir, 'raw.json'), join(dir, 'out.json')], { encoding: 'utf8', env: { ...process.env, ...ENV } });
      assert.equal(ok.status, 0, ok.stderr);
      const summary = JSON.parse(ok.stdout);
      assert.equal(summary.name, 'agent-review-result-copilot-38050149174');
      assert.match(summary.digest, /^[0-9a-f]{64}$/);
      assert.equal(JSON.parse(readFileSync(join(dir, 'out.json'), 'utf8')).head_sha, SHA);

      const missing = spawnSync(process.execPath, [script, 'package', join(dir, 'absent.json'), join(dir, 'out2.json')], { encoding: 'utf8', env: { ...process.env, ...ENV } });
      assert.equal(missing.status, 2);
      assert.equal(missing.stderr.trim(), 'empty: The reviewer produced no structured output.');
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
