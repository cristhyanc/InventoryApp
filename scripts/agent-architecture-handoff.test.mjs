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
import { claimEvents } from './agent-mode.fixtures.mjs';

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8').replaceAll('\r\n', '\n');

function stepShell(workflow, name) {
  const step = workflow.split(`      - name: ${name}\n`)[1]?.split('\n      - name: ')[0];
  assert.ok(step, `missing trusted step: ${name}`);
  // The last step of a job ends where the next job begins.
  const run = step.split('        run: |\n')[1]?.split(/\n\n {2}[a-z]/)[0];
  assert.ok(run, `step has no run block: ${name}`);
  return run.split('\n').map((line) => line.slice(10)).join('\n');
}

const dispatchShell = stepShell(read('.github/workflows/agent-implement.yml'), 'Verify pull request and dispatch trusted architecture workflow');
const finalizeShell = stepShell(read('.github/workflows/agent-architecture.yml'), 'Record architecture outcome and dispatch exact-SHA validation');
const contextShell = stepShell(read('.github/workflows/agent-architecture.yml'), 'Verify exact agent pull request');

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
const claimEvents = ${claimEvents.toString()};
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
if (args[0] === 'pr' && args[1] === 'view') out(pick({ createdAt: '2026-10-03T02:00:00Z', ...state.pr }));
else if (args[0] === 'issue' && args[1] === 'view') out(pick(state.issue));
else if (['edit', 'comment'].includes(args[1]) && ['pr', 'issue'].includes(args[0])) log({ kind: args[0] + '-' + args[1], args });
else if (args[0] === 'workflow' && args[1] === 'run') log({ kind: 'dispatch', workflow: args[2], args });
else if (args[0] === 'api') {
  const path = args.find((a, i) => i > 0 && !a.startsWith('-') && !['--jq', '-H'].includes(args[i - 1]));
  if (/\\/commits\\/[0-9a-f]{40}\\/status$/.test(path)) out({ statuses: state.statuses[path.split('/commits/')[1].split('/')[0]] ?? [] });
  else if (path.includes('/files')) out(state.files.map((filename) => ({ filename })));
  else if (/\\/pulls\\/\\d+$/.test(path)) out({ user: { login: state.author } });
  else if (path.includes('/contents/scripts/agent-mode.mjs')) process.stdout.write(fs.readFileSync(process.env.AGENT_MODE_SCRIPT, 'utf8'));
  else if (/\\/issues\\/\\d+\\/events/.test(path)) out(state.events ?? claimEvents(state.pr.headRefName.startsWith('copilot/') ? 'agent-ready-copilot' : 'agent-ready-claude'));
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
    const outputPath = join(root, 'github-output');
    writeFileSync(statePath, JSON.stringify(state));
    writeFileSync(outputPath, '');
    const result = spawnSync('bash', ['-c', shell], {
      encoding: 'utf8',
      env: {
        ...process.env,
        PATH: `${bin}:${process.env.PATH}`,
        AGENT_MODE_GH_PATH: join(bin, 'gh'),
        FAKE_GH_STATE: statePath,
        FAKE_GH_LOG: logPath,
        GITHUB_OUTPUT: outputPath,
        GITHUB_REPOSITORY: REPO,
        RUNNER_TEMP: root,
        GITHUB_WORKFLOW_SHA: 'a'.repeat(40),
        AGENT_MODE_SCRIPT: new URL('./agent-mode.mjs', import.meta.url).pathname,
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
    return { status: result.status, stderr: result.stderr, calls, outputs: readFileSync(outputPath, 'utf8') };
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

describe('provider mode at the architecture handoff', () => {
  const env = { HEAD_SHA: SHA };
  it('dispatches when the issue was claimed as Claude-primary', () => {
    const { status, stderr, calls } = run(dispatchShell, { state: { ...fixture(), events: claimEvents('agent-ready-claude') }, env });
    assert.equal(status, 0, stderr);
    assert.equal(dispatches(calls, 'agent-architecture.yml').length, 1);
  });

  it('dispatches when the issue was claimed for the full-claude fallback', () => {
    const { status, stderr, calls } = run(dispatchShell, { state: { ...fixture(), events: claimEvents('agent-ready-full-claude') }, env });
    assert.equal(status, 0, stderr);
    assert.equal(dispatches(calls, 'agent-architecture.yml').length, 1);
  });

  for (const [name, events] of [
    ['last claimed for Copilot', claimEvents('agent-ready-copilot')],
    ['last claimed for the full-copilot fallback', claimEvents('agent-ready-full-copilot')],
    ['never claimed', []],
    ['claimed again after this pull request was opened', claimEvents('agent-ready-claude', '2026-10-03T03:00:00Z')],
  ]) {
    it(`blocks instead of dispatching when the issue was ${name}`, () => {
      const { status, calls } = run(dispatchShell, { state: { ...fixture(), events }, env });
      assert.notEqual(status, 0);
      assert.equal(dispatches(calls, 'agent-architecture.yml').length, 0);
      assert.ok(blocked(calls));
    });
  }
});

describe('architecture checker selected by the verified route', () => {
  // Four-mode matrix on the Claude-implementer side: only the Claude routes reach this workflow, and
  // each names exactly one checker; the Copilot routes fail closed and name none.
  for (const [label, checker] of [['agent-ready-claude', 'copilot'], ['agent-ready-claude-high', 'copilot'], ['agent-ready-full-claude', 'claude']]) {
    it(`${label} selects the ${checker} architecture check`, () => {
      const { status, stderr, outputs } = run(contextShell, { state: { ...fixture(), events: claimEvents(label) }, env: { HEAD_SHA: SHA } });
      assert.equal(status, 0, stderr);
      assert.match(outputs, new RegExp(`^checker=${checker}$`, 'm'));
      assert.match(outputs, new RegExp(`^agent_mode=${label === 'agent-ready-full-claude' ? 'full-claude' : 'cross-claude'}$`, 'm'));
      if (checker === 'claude') assert.match(outputs, /^checker_name=.*same-provider check, not an independent one/m);
      else assert.match(outputs, /^checker_name=GitHub Copilot, a different agent,$/m);
    });
  }
  for (const label of ['agent-ready-copilot', 'agent-ready-full-copilot']) {
    it(`${label} names no checker and fails closed`, () => {
      const { status, outputs } = run(contextShell, { state: { ...fixture(), events: claimEvents(label) }, env: { HEAD_SHA: SHA } });
      assert.notEqual(status, 0);
      assert.doesNotMatch(outputs, /^checker=/m);
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
  // Copilot's read-only check reported findings and Claude's fix pass produced the final head.
  const success = {
    CONTEXT_JOB_RESULT: 'success',
    CHECKER: 'copilot',
    CHECK_JOB_RESULT: 'success',
    CHECK_VERDICT: 'findings',
    CHECK_FINDINGS: 'backend/Api/ProductsController.cs:12 calls the repository directly.\nARCHITECTURE: FINDINGS',
    ARCHITECTURE_JOB_RESULT: 'success',
    EXPECTED_START_SHA: SHA,
    FIX_SHA: SHA,
  };
  // Copilot reported a clean architecture, so Claude's fix pass was skipped.
  const clean = { ...success, CHECK_VERDICT: 'clean', CHECK_FINDINGS: 'ARCHITECTURE: CLEAN', ARCHITECTURE_JOB_RESULT: 'skipped', FIX_SHA: '' };
  // Full-claude: the separate read-only Claude check reported findings; the record says same-provider.
  const sameProvider = { ...success, CHECKER: 'claude', CHECK_FINDINGS: '- `backend/Api/ProductsController.cs:12` (Use cases): calls the repository directly.' };

  it('records a same-provider Claude check as not independent and dispatches validation', () => {
    for (const env of [sameProvider, { ...sameProvider, CHECK_VERDICT: 'clean', CHECK_FINDINGS: '', ARCHITECTURE_JOB_RESULT: 'skipped', FIX_SHA: '' }]) {
      const { status, stderr, calls } = run(finalizeShell, { state: fixture(), env });
      assert.equal(status, 0, stderr);
      assert.equal(dispatches(calls, 'validate.yml').length, 1);
      const comment = calls.find((c) => c.kind === 'pr-comment');
      assert.ok(comment);
      assert.ok(!blocked(calls));
    }
  });

  it('labels for review and dispatches exact-SHA validation after Claude fixes Copilot findings', () => {
    const { status, calls } = run(finalizeShell, { state: fixture(), env: success });
    assert.equal(status, 0);
    assert.equal(dispatches(calls, 'validate.yml').length, 1);
    assert.ok(dispatches(calls, 'validate.yml')[0].args.includes(`head_sha=${SHA}`));
    assert.ok(calls.some((c) => c.kind === 'pr-comment'), 'Copilot findings must be recorded on the PR');
    assert.ok(!blocked(calls));
  });

  it('dispatches validation of the unchanged head when Copilot reports a clean architecture', () => {
    const { status, calls } = run(finalizeShell, { state: fixture(), env: clean });
    assert.equal(status, 0);
    assert.equal(dispatches(calls, 'validate.yml').length, 1);
    assert.ok(dispatches(calls, 'validate.yml')[0].args.includes(`head_sha=${SHA}`));
    assert.ok(!blocked(calls));
  });

  it('accepts Claude\'s fix head when only SonarCloud reported issues', () => {
    const sonarOnly = { ...clean, SONAR_STATUS: 'analysed', SONAR_COUNT: '2', SONAR_ISSUES: '- `a.cs:1` [S1] (CODE_SMELL): x', ARCHITECTURE_JOB_RESULT: 'success', FIX_SHA: SHA };
    const { status, calls } = run(finalizeShell, { state: fixture(), env: sonarOnly });
    assert.equal(status, 0);
    assert.equal(dispatches(calls, 'validate.yml').length, 1);
    const comment = calls.find((c) => c.kind === 'pr-comment');
    assert.ok(comment, 'the SonarCloud issues must be recorded on the PR');
    assert.ok(!blocked(calls));
  });

  it('treats an unavailable SonarCloud read as no issues', () => {
    for (const sonar of [{ SONAR_STATUS: 'unavailable', SONAR_COUNT: '0' }, { SONAR_COUNT: '' }, { SONAR_COUNT: 'oops' }]) {
      const { status, calls } = run(finalizeShell, { state: fixture(), env: { ...clean, ...sonar } });
      assert.equal(status, 0);
      assert.equal(dispatches(calls, 'validate.yml').length, 1);
      assert.ok(!blocked(calls));
    }
  });

  it('does not dispatch validation twice when the SHA already has an agent-validation status', () => {
    const state = fixture({ statuses: { [SHA]: [{ context: 'agent-validation', state: 'pending' }] } });
    const { status, calls } = run(finalizeShell, { state, env: success });
    assert.equal(status, 0);
    assert.equal(dispatches(calls, 'validate.yml').length, 0);
    assert.ok(!blocked(calls));
  });

  for (const [name, env] of [
    ['a rejected target check', { ...success, CONTEXT_JOB_RESULT: 'failure', CHECKER: '', CHECK_JOB_RESULT: 'skipped', ARCHITECTURE_JOB_RESULT: 'skipped', FIX_SHA: '' }],
    ['a failed Copilot check', { ...success, CHECK_JOB_RESULT: 'failure', CHECK_VERDICT: '', ARCHITECTURE_JOB_RESULT: 'skipped', FIX_SHA: '' }],
    ['a failed same-provider Claude check (no fallback to Copilot)', { ...sameProvider, CHECK_JOB_RESULT: 'failure', CHECK_VERDICT: '', ARCHITECTURE_JOB_RESULT: 'skipped', FIX_SHA: '' }],
    ['a skipped checker for the route', { ...sameProvider, CHECK_JOB_RESULT: 'skipped', CHECK_VERDICT: '', ARCHITECTURE_JOB_RESULT: 'skipped', FIX_SHA: '' }],
    ['an unknown checker', { ...success, CHECKER: 'gemini' }],
    ['an unknown Copilot verdict', { ...success, CHECK_VERDICT: 'maybe' }],
    ['a failed Claude fix pass', { ...success, ARCHITECTURE_JOB_RESULT: 'failure', FIX_SHA: '' }],
    ['a clean verdict that still ran the fix pass', { ...clean, ARCHITECTURE_JOB_RESULT: 'success', FIX_SHA: NEWER_SHA }],
    ['SonarCloud issues whose fix pass failed', { ...clean, SONAR_COUNT: '1', ARCHITECTURE_JOB_RESULT: 'failure' }],
    ['SonarCloud issues whose fix pass was skipped', { ...clean, SONAR_COUNT: '1' }],
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

  it('blocks when the PR head moved during the architecture stage', () => {
    for (const env of [success, clean]) {
      const { status, calls } = run(finalizeShell, { state: fixture({ pr: { headRefOid: NEWER_SHA } }), env });
      assert.notEqual(status, 0);
      assert.ok(blocked(calls));
    }
  });

  it('never blocks an issue that is not agent-working, such as one already in review', () => {
    const state = fixture({ issue: { labels: [{ name: 'agent-review' }] } });
    const { status, calls } = run(finalizeShell, { state, env: { ...success, CONTEXT_JOB_RESULT: 'failure', FIX_SHA: '' } });
    assert.notEqual(status, 0);
    assert.ok(!blocked(calls));
    assert.equal(calls.length, 0);
  });
});
