// Behavioural tests for the implementation-to-architecture handoff (agent-implement.yml
// dispatch-architecture job) and the architecture finalizer (agent-architecture.yml finalize job).
//
// Each test extracts the exact shell of the trusted step and runs it with bash against a fake `gh`
// that serves a fixture of the live issue and pull request and records every call. They prove that
// every failure before validation is dispatched blocks the task instead of leaving it agent-working.
import assert from 'node:assert/strict';
import { chmodSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { describe, it } from 'node:test';

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8').replaceAll('\r\n', '\n');

function stepShell(workflow, name) {
  const step = workflow.split(`      - name: ${name}\n`)[1]?.split('\n      - name: ')[0];
  assert.ok(step, `missing trusted step: ${name}`);
  const run = step.split('        run: |\n')[1];
  assert.ok(run, `step has no run block: ${name}`);
  return run.split('\n').map((line) => line.slice(10)).join('\n');
}

const dispatchShell = stepShell(read('.github/workflows/agent-implement.yml'), 'Verify pull request and dispatch trusted architecture workflow');
const finalizeShell = stepShell(read('.github/workflows/agent-architecture.yml'), 'Record architecture outcome and dispatch exact-SHA validation');

const REPO = 'owner/InventoryApp';
const BOT = 'inventoryapp-agent-automation[bot]';
const ISSUE = '240';
const PR = '275';
const SHA = '6b5ade799d290fb2c4e59710cbf047c43a305df0';
const NEWER_SHA = 'f49fcc313ab6295c29d9d426445e1dbbd54fd997';

// A fake gh CLI: serves the fixture in $FAKE_GH_STATE and appends every mutating call to $FAKE_GH_LOG.
const FAKE_GH = `#!/usr/bin/env node
const fs = require('node:fs');
const { spawnSync } = require('node:child_process');
const args = process.argv.slice(2);
const state = JSON.parse(fs.readFileSync(process.env.FAKE_GH_STATE, 'utf8'));
const log = (entry) => fs.appendFileSync(process.env.FAKE_GH_LOG, JSON.stringify(entry) + '\\n');
const opt = (name) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const out = (value) => {
  const expr = opt('--jq');
  if (expr === undefined) { process.stdout.write(JSON.stringify(value)); return; }
  const r = spawnSync('jq', ['-r', expr], { input: JSON.stringify(value), encoding: 'utf8' });
  if (r.status !== 0) { process.stderr.write(r.stderr); process.exit(1); }
  process.stdout.write(r.stdout);
};
const fail = (why) => { process.stderr.write(why + '\\n'); process.exit(1); };
if ((state.unavailable ?? []).some((prefix) => args.join(' ').startsWith(prefix))) fail('HTTP 502: fixture outage');
const pick = (obj) => Object.fromEntries(opt('--json').split(',').map((f) => [f, obj[f]]));
if (args[0] === 'pr' && args[1] === 'view') out(pick(state.pr));
else if (args[0] === 'issue' && args[1] === 'view') out(pick(state.issue));
else if (['edit', 'comment'].includes(args[1]) && ['pr', 'issue'].includes(args[0])) log({ kind: args[0] + '-' + args[1], args });
else if (args[0] === 'workflow' && args[1] === 'run') log({ kind: 'dispatch', workflow: args[2], args });
else if (args[0] === 'api') {
  const path = args.find((a, i) => i > 0 && !a.startsWith('-') && args[i - 1] !== '--jq');
  if (/\\/commits\\/[0-9a-f]{40}\\/status$/.test(path)) out({ statuses: state.statuses[path.split('/commits/')[1].split('/')[0]] ?? [] });
  else if (path.includes('/files')) out(state.files.map((filename) => ({ filename })));
  else if (/\\/pulls\\/\\d+$/.test(path)) out({ user: { login: state.author } });
  else fail('unexpected api call: ' + args.join(' '));
} else fail('unexpected gh call: ' + args.join(' '));
`;

function fixture(overrides = {}) {
  return {
    pr: {
      state: 'OPEN',
      isDraft: false,
      baseRefName: 'develop',
      headRefName: `agent/issue-${ISSUE}-products-clean-architecture`,
      headRefOid: SHA,
      headRepository: { name: 'InventoryApp' },
      headRepositoryOwner: { login: 'owner' },
      ...(overrides.pr ?? {}),
    },
    issue: { state: 'OPEN', labels: [{ name: 'agent-working' }], ...(overrides.issue ?? {}) },
    author: overrides.author ?? BOT,
    files: overrides.files ?? ['backend/Inventory.Domain/Products/Product.cs'],
    statuses: overrides.statuses ?? {},
    unavailable: overrides.unavailable ?? [],
  };
}

function run(shell, { state, env = {} }) {
  const root = mkdtempSync(join(tmpdir(), 'agent-architecture-handoff-'));
  try {
    const bin = join(root, 'bin');
    spawnSync('mkdir', [bin]);
    writeFileSync(join(bin, 'gh'), FAKE_GH);
    chmodSync(join(bin, 'gh'), 0o755);
    const statePath = join(root, 'state.json');
    const logPath = join(root, 'calls.jsonl');
    writeFileSync(statePath, JSON.stringify(state));
    const result = spawnSync('bash', ['-c', shell], {
      encoding: 'utf8',
      env: {
        ...process.env,
        PATH: `${bin}:${process.env.PATH}`,
        FAKE_GH_STATE: statePath,
        FAKE_GH_LOG: logPath,
        GITHUB_REPOSITORY: REPO,
        GH_TOKEN: 'fixture-token',
        EXPECTED_AGENT_AUTHOR: BOT,
        ISSUE_NUMBER: ISSUE,
        PR_NUMBER: PR,
        RUN_URL: 'https://github.com/owner/InventoryApp/actions/runs/1',
        ...env,
      },
    });
    const calls = existsSync(logPath)
      ? readFileSync(logPath, 'utf8').trim().split('\n').filter(Boolean).map((line) => JSON.parse(line))
      : [];
    return { status: result.status, stderr: result.stderr, calls };
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const blocked = (calls) => calls.some((c) => c.kind === 'issue-edit' && c.args.includes('agent-blocked'));
const commented = (calls) => calls.some((c) => c.kind === 'issue-comment');
const dispatches = (calls, workflow) => calls.filter((c) => c.kind === 'dispatch' && c.workflow === workflow);

describe('implementation to architecture handoff', () => {
  const env = { HEAD_SHA: SHA };

  it('dispatches the architecture workflow for an eligible PR without touching labels', () => {
    const { status, calls } = run(dispatchShell, { state: fixture(), env });
    assert.equal(status, 0);
    assert.equal(dispatches(calls, 'agent-architecture.yml').length, 1);
    assert.ok(!blocked(calls));
  });

  for (const [name, overrides] of [
    ['a stale PR head', { pr: { headRefOid: NEWER_SHA } }],
    ['a closed PR', { pr: { state: 'CLOSED' } }],
    ['a PR that changes workflows', { files: ['.github/workflows/agent-implement.yml'] }],
    ['an unavailable PR lookup', { unavailable: ['pr view'] }],
    ['a failed dispatch', { unavailable: ['workflow run'] }],
  ]) {
    it(`blocks the issue and comments when the handoff fails on ${name}`, () => {
      const { status, calls } = run(dispatchShell, { state: fixture(overrides), env });
      assert.notEqual(status, 0);
      assert.ok(blocked(calls), 'issue must move to agent-blocked');
      assert.ok(commented(calls), 'the failure comment must post (RUN_URL is defined)');
    });
  }
});

describe('stale implementation dispatcher', () => {
  it('never blocks an issue that has already moved on from agent-working', () => {
    for (const labels of [[{ name: 'agent-review' }], [{ name: 'agent-blocked' }]]) {
      const state = fixture({ pr: { headRefOid: NEWER_SHA }, issue: { labels } });
      const { status, calls } = run(dispatchShell, { state, env: { HEAD_SHA: SHA } });
      assert.notEqual(status, 0);
      assert.equal(calls.length, 0, 'no label edit or comment on an issue that is not agent-working');
    }
  });

  it('never blocks a closed issue', () => {
    const state = fixture({ unavailable: ['workflow run'], issue: { state: 'CLOSED' } });
    const { status, calls } = run(dispatchShell, { state, env: { HEAD_SHA: SHA } });
    assert.notEqual(status, 0);
    assert.ok(!blocked(calls));
  });
});

describe('architecture finalizer', () => {
  const success = { CONTEXT_JOB_RESULT: 'success', ARCHITECTURE_JOB_RESULT: 'success', EXPECTED_START_SHA: SHA, FINAL_SHA: SHA };

  it('labels for review and dispatches exact-SHA validation after a successful pass', () => {
    const { status, calls } = run(finalizeShell, { state: fixture(), env: success });
    assert.equal(status, 0);
    assert.equal(dispatches(calls, 'validate.yml').length, 1);
    assert.ok(dispatches(calls, 'validate.yml')[0].args.includes(`head_sha=${SHA}`));
    assert.ok(!blocked(calls));
  });

  it('does not dispatch validation twice when the SHA already has an agent-validation status', () => {
    const state = fixture({ statuses: { [SHA]: [{ context: 'agent-validation', state: 'pending' }] } });
    const { status, calls } = run(finalizeShell, { state, env: success });
    assert.equal(status, 0);
    assert.equal(dispatches(calls, 'validate.yml').length, 0);
    assert.ok(!blocked(calls));
  });

  for (const [name, env] of [
    ['a rejected target check', { ...success, CONTEXT_JOB_RESULT: 'failure', ARCHITECTURE_JOB_RESULT: 'skipped', FINAL_SHA: '' }],
    ['a failed architecture job', { ...success, ARCHITECTURE_JOB_RESULT: 'failure', FINAL_SHA: '' }],
  ]) {
    it(`blocks an agent-working issue on ${name}`, () => {
      const { status, calls } = run(finalizeShell, { state: fixture(), env });
      assert.notEqual(status, 0);
      assert.ok(blocked(calls));
      assert.ok(commented(calls));
      assert.equal(dispatches(calls, 'validate.yml').length, 0);
    });
  }

  it('blocks and removes agent-review when validation dispatch fails after labelling', () => {
    const { status, calls } = run(finalizeShell, { state: fixture({ unavailable: ['workflow run'] }), env: success });
    assert.notEqual(status, 0);
    assert.ok(blocked(calls));
    assert.ok(calls.some((c) => c.kind === 'pr-edit' && c.args.includes('--remove-label') && c.args.includes('agent-review')));
  });

  it('blocks when the PR head moved during the architecture pass', () => {
    const { status, calls } = run(finalizeShell, { state: fixture({ pr: { headRefOid: NEWER_SHA } }), env: success });
    assert.notEqual(status, 0);
    assert.ok(blocked(calls));
  });

  it('never blocks an issue that is not agent-working, such as one already in review', () => {
    const state = fixture({ issue: { labels: [{ name: 'agent-review' }] } });
    const { status, calls } = run(finalizeShell, { state, env: { ...success, CONTEXT_JOB_RESULT: 'failure', FINAL_SHA: '' } });
    assert.notEqual(status, 0);
    assert.ok(!blocked(calls));
    assert.equal(calls.length, 0);
  });
});
