// Unit and command-line tests for the shared agent pull request eligibility guard
// (scripts/agent-pr-guard.mjs). The workflow-level behaviour of each caller (fail, skip or
// suppress) is covered in agent-review-publication.test.mjs and agent-architecture-handoff.test.mjs.
import assert from 'node:assert/strict';
import { chmodSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { describe, it } from 'node:test';
import { GuardRefusal, REFUSAL_CODES, evaluate, parseRequest } from './agent-pr-guard.mjs';

const REPO = 'owner/InventoryApp';
const BOT = 'inventoryapp-agent-automation[bot]';
const SHA = '6b5ade799d290fb2c4e59710cbf047c43a305df0';
const NEWER_SHA = 'f49fcc313ab6295c29d9d426445e1dbbd54fd997';
const GUARD = new URL('./agent-pr-guard.mjs', import.meta.url).pathname;

function live(overrides = {}) {
  return {
    pr: {
      state: 'OPEN',
      isDraft: false,
      baseRefName: 'develop',
      headRefName: 'agent/issue-245-take-inventory-page',
      headRefOid: SHA,
      headRepository: { name: 'InventoryApp' },
      headRepositoryOwner: { login: 'owner' },
      labels: [{ name: 'agent-review' }, { name: 'backend' }],
      ...(overrides.pr ?? {}),
    },
    author: overrides.author ?? BOT,
    files: overrides.files ?? ['backend/InventoryApi/Services/InventoryCountService.cs'],
  };
}

const request = (overrides = {}) => ({ repository: REPO, pr: '267', sha: SHA, issue: undefined, ...overrides });

function refusal(fn) {
  try {
    fn();
  } catch (error) {
    assert.ok(error instanceof GuardRefusal, `expected a refusal, got ${error}`);
    return error;
  }
  assert.fail('expected a refusal');
}

describe('agent pull request guard: decision', () => {
  it('accepts an eligible pull request and reports its branch, head, implementer and labels', () => {
    assert.deepEqual(evaluate(request(), live(), BOT), {
      base_ref: 'develop',
      head_ref: 'agent/issue-245-take-inventory-page',
      head_sha: SHA,
      implementer: 'claude',
      labels: ['agent-review', 'backend'],
    });
    assert.equal(evaluate(request({ issue: '245' }), live(), BOT).head_ref, 'agent/issue-245-take-inventory-page');
  });

  for (const [code, state, req = {}, expected = BOT] of [
    ['closed', { pr: { state: 'CLOSED' } }],
    ['closed', { pr: { state: 'MERGED' } }],
    ['draft', { pr: { isDraft: true } }],
    ['draft', { pr: { isDraft: undefined } }],
    ['base', { pr: { baseRefName: 'main' } }],
    ['fork', { pr: { headRepositoryOwner: { login: 'someone' } } }],
    ['fork', { pr: { headRepository: null, headRepositoryOwner: null } }],
    ['branch', { pr: { headRefName: 'copilot/fix-245-take-inventory-page' } }],
    ['branch', { pr: { headRefName: 'feature/manual' } }],
    ['branch', {}, { issue: '246' }],
    ['branch', { pr: { headRefName: 'agent/issue-2450-other' } }, { issue: '245' }],
    ['config', {}, {}, ''],
    ['author', { author: 'someone-else' }],
    ['author', { author: '' }],
    ['stale', { pr: { headRefOid: NEWER_SHA } }],
    ['stale', { pr: { headRefOid: null } }],
    ['workflow-files', { files: ['docs/automation.md', '.github/workflows/agent-review.yml'] }],
  ]) {
    it(`refuses with '${code}' for ${JSON.stringify({ ...state, ...req, expected })}`, () => {
      const error = refusal(() => evaluate(request(req), live(state), expected));
      assert.equal(error.code, code);
      assert.match(error.message, /\.$/);
    });
  }

  it('reports the first failed check in its documented order', () => {
    const everything = live({ pr: { state: 'CLOSED', isDraft: true, headRefOid: NEWER_SHA }, author: 'x', files: ['.github/workflows/a.yml'] });
    assert.equal(refusal(() => evaluate(request(), everything, BOT)).code, 'closed');
    const notStale = live({ pr: { headRefOid: NEWER_SHA }, files: ['.github/workflows/a.yml'] });
    assert.equal(refusal(() => evaluate(request(), notStale, BOT)).code, 'stale');
    // A workflow path elsewhere in the tree is not a workflow change.
    assert.doesNotThrow(() => evaluate(request(), live({ files: ['docs/.github/workflows/x.yml', '.github/ISSUE_TEMPLATE/task.yml'] }), BOT));
  });

  it('validates its input before reading anything', () => {
    for (const bad of [{ pr: '0' }, { pr: '12a' }, { sha: SHA.toUpperCase() }, { sha: SHA.slice(1) }, { issue: '-1' }, { repository: 'not a repo' }]) {
      assert.equal(refusal(() => parseRequest(request(bad))).code, 'input');
    }
    assert.deepEqual(parseRequest(request({ pr: 267, issue: 245 })), { repository: REPO, pr: '267', sha: SHA, issue: '245' });
  });

  it('uses only documented refusal codes', () => {
    assert.throws(() => new GuardRefusal('maybe', 'x'), /Unknown refusal code/);
    assert.ok(REFUSAL_CODES.includes('workflow-files'));
  });
});

// A fake gh that serves the live state and fails any call whose arguments contain `unavailable`.
const FAKE_GH = `#!/usr/bin/env node
const fs = require('node:fs');
const { spawnSync } = require('node:child_process');
const args = process.argv.slice(2);
const state = JSON.parse(fs.readFileSync(process.env.FAKE_GH_STATE, 'utf8'));
const opt = (name) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const out = (value) => {
  const expr = opt('--jq');
  if (expr === undefined) { process.stdout.write(JSON.stringify(value)); return; }
  const r = spawnSync('jq', ['-r', expr], { input: JSON.stringify(value), encoding: 'utf8' });
  process.stdout.write(r.stdout);
};
if ((state.unavailable ?? []).some((part) => args.join(' ').includes(part))) { process.stderr.write('HTTP 502\\n'); process.exit(1); }
if (args[0] === 'pr' && args[1] === 'view') {
  if (state.malformed) { process.stdout.write('not json'); process.exit(0); }
  out(Object.fromEntries(opt('--json').split(',').map((f) => [f, state.pr[f]])));
} else if (args[0] === 'api' && args.some((a) => a.includes('/files'))) out(state.files.map((filename) => ({ filename })));
else if (args[0] === 'api') out({ user: { login: state.author } });
else process.exit(3);
`;

function cli(args, { state = live(), env = {} } = {}) {
  const root = mkdtempSync(join(tmpdir(), 'agent-pr-guard-'));
  try {
    const gh = join(root, 'gh');
    writeFileSync(gh, FAKE_GH);
    chmodSync(gh, 0o755);
    writeFileSync(join(root, 'state.json'), JSON.stringify(state));
    const result = spawnSync('node', [GUARD, ...args], {
      encoding: 'utf8',
      env: { ...process.env, AGENT_PR_GUARD_GH_PATH: gh, FAKE_GH_STATE: join(root, 'state.json'), GITHUB_REPOSITORY: REPO, EXPECTED_AGENT_AUTHOR: BOT, ...env },
    });
    return { status: result.status, stdout: result.stdout, stderr: result.stderr };
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

describe('agent pull request guard: command line', () => {
  it('prints the eligible pull request as JSON and exits 0', () => {
    const outcome = cli(['check', '267', SHA, '--issue', '245']);
    assert.equal(outcome.status, 0, outcome.stderr);
    assert.equal(outcome.stderr, '');
    assert.deepEqual(JSON.parse(outcome.stdout).labels, ['agent-review', 'backend']);
  });

  it('prints one "code: message" line and exits 2 on a refusal', () => {
    const outcome = cli(['check', '267', SHA], { state: live({ files: ['.github/workflows/validate.yml'] }) });
    assert.equal(outcome.status, 2);
    assert.equal(outcome.stdout, '');
    assert.equal(outcome.stderr, 'workflow-files: pull requests that change .github/workflows/** require manual validation and review.\n');
    const stale = cli(['check', '267', SHA], { state: live({ pr: { headRefOid: NEWER_SHA } }) });
    assert.match(stale.stderr, /^stale: the head of pull request #267 is now f49fcc3\w+, not 6b5ade7\w+\.\n$/);
  });

  it('refuses as unavailable when any live read fails or is malformed, never as eligible', () => {
    for (const state of [
      { ...live(), unavailable: ['pr view'] },
      { ...live(), unavailable: ['pulls/267/files'] },
      { ...live(), unavailable: ['pulls/267 '] },
      { ...live(), malformed: true },
    ]) {
      const outcome = cli(['check', '267', SHA], { state });
      assert.equal(outcome.status, 2);
      assert.equal(outcome.stdout, '');
      assert.match(outcome.stderr, /^unavailable: the live state of pull request #267 could not be read\.\n$/);
    }
  });

  it('refuses malformed invocations and a missing App login', () => {
    for (const args of [[], ['view', '267', SHA], ['check', '267'], ['check', '267', SHA, '--issue'], ['check', '267', SHA, '--force'], ['check', '267', SHA, '--issue', '1', '--issue', '2']]) {
      const outcome = cli(args);
      assert.equal(outcome.status, 2, JSON.stringify(args));
      assert.match(outcome.stderr, /^input: /);
    }
    assert.match(cli(['check', '267', SHA], { env: { EXPECTED_AGENT_AUTHOR: '' } }).stderr, /^config: AGENT_AUTOMATION_APP_BOT_LOGIN is not configured\.\n$/);
    assert.match(cli(['check', '267', SHA], { env: { GITHUB_REPOSITORY: '' } }).stderr, /^input: /);
  });
});
