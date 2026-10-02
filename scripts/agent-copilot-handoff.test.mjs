// Behavioural tests for the agent-ready-copilot path: the Copilot pull request handoff
// (agent-copilot-handoff.yml) and the finalizer of Claude's read-only architecture check
// (agent-copilot-architecture.yml finalize job).
//
// Each test extracts the exact shell of the trusted step and runs it with bash against a fake `gh`
// that serves a fixture of the live issue and pull request and records every call, including the
// token each call used. They prove that the handoff only accepts Copilot's own work, that findings
// go back to Copilot rather than being fixed by Claude, and that every failure before the handoff
// blocks the task instead of leaving it agent-working.
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

const handoffShell = stepShell(read('.github/workflows/agent-copilot-handoff.yml'), 'Verify Copilot pull request and dispatch architecture check');
const finalizeShell = stepShell(read('.github/workflows/agent-copilot-architecture.yml'), 'Record architecture outcome and hand off');

const REPO = 'owner/InventoryApp';
const COPILOT = 'Copilot';
const ISSUE = '281';
const PR = '290';
const SHA = '6b5ade799d290fb2c4e59710cbf047c43a305df0';
const NEWER_SHA = 'f49fcc313ab6295c29d9d426445e1dbbd54fd997';

const FAKE_GH = `#!/usr/bin/env node
const fs = require('node:fs');
const { spawnSync } = require('node:child_process');
const args = process.argv.slice(2);
const state = JSON.parse(fs.readFileSync(process.env.FAKE_GH_STATE, 'utf8'));
const log = (entry) => fs.appendFileSync(process.env.FAKE_GH_LOG, JSON.stringify({ ...entry, token: process.env.GH_TOKEN }) + '\\n');
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
else if (args[0] === 'pr' && args[1] === 'ready') log({ kind: 'pr-ready', args });
else if (args[0] === 'issue' && args[1] === 'view') out(pick(state.issue));
else if (['edit', 'comment'].includes(args[1]) && ['pr', 'issue'].includes(args[0])) {
  const bodyFile = opt('--body-file');
  log({ kind: args[0] + '-' + args[1], args, body: bodyFile ? fs.readFileSync(bodyFile, 'utf8') : opt('--body') });
} else if (args[0] === 'workflow' && args[1] === 'run') log({ kind: 'dispatch', workflow: args[2], args });
else if (args[0] === 'api') {
  const path = args.find((a, i) => i > 0 && !a.startsWith('-') && args[i - 1] !== '--jq');
  if (path.includes('/actions/runs?')) out({ workflow_runs: state.runs ?? [] });
  else if (/\\/commits\\/[0-9a-f]{40}\\/status$/.test(path)) out({ statuses: state.statuses[path.split('/commits/')[1].split('/')[0]] ?? [] });
  else if (path.includes('/files')) out(state.files.map((filename) => ({ filename })));
  else if (/\\/pulls\\/\\d+$/.test(path)) out({ user: { login: state.author } });
  else fail('unexpected api call: ' + args.join(' '));
} else fail('unexpected gh call: ' + args.join(' '));
`;

// GitHub lists Copilot sessions as dynamic runs with an empty pull_requests list.
function copilotRun(fields) {
  return {
    name: 'Running Copilot cloud agent',
    event: 'dynamic',
    path: 'dynamic/copilot-swe-agent/copilot',
    head_branch: 'copilot/fix-281',
    created_at: '2026-10-02T12:00:00Z',
    pull_requests: [],
    ...fields,
  };
}

function fixture(overrides = {}) {
  return {
    pr: {
      state: 'OPEN',
      isDraft: false,
      baseRefName: 'develop',
      headRefName: 'copilot/fix-281',
      headRefOid: SHA,
      headRepository: { name: 'InventoryApp' },
      headRepositoryOwner: { login: 'owner' },
      closingIssuesReferences: [{ number: Number(ISSUE) }],
      ...(overrides.pr ?? {}),
    },
    issue: { state: 'OPEN', labels: [{ name: 'agent-working' }], ...(overrides.issue ?? {}) },
    author: overrides.author ?? COPILOT,
    files: overrides.files ?? ['backend/Inventory.Application/Products/ListProducts.cs'],
    statuses: overrides.statuses ?? { [SHA]: [{ context: 'merge-validation', state: 'success' }] },
    runs: overrides.runs ?? [copilotRun({ status: 'completed', conclusion: 'success' })],
    unavailable: overrides.unavailable ?? [],
  };
}

function run(shell, { state, env = {} }) {
  const root = mkdtempSync(join(tmpdir(), 'agent-copilot-handoff-'));
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
        GH_TOKEN: 'workflow-token',
        COPILOT_AGENT_TOKEN: 'owner-copilot-token',
        EXPECTED_COPILOT_AUTHOR: COPILOT,
        PR_NUMBER: PR,
        HEAD_SHA: SHA,
        RUN_URL: 'https://github.com/owner/InventoryApp/actions/runs/1',
        GITHUB_EVENT_NAME: 'pull_request_target',
        EVENT_PR_NUMBER: PR,
        EVENT_HEAD_SHA: SHA,
        RUN_PR_NUMBER: '',
        RUN_HEAD_SHA: '',
        RUN_EVENT: '',
        RUN_CONCLUSION: '',
        RUN_PATH: '',
        COPILOT_RUN_POLL_ATTEMPTS: '1',
        COPILOT_RUN_POLL_SECONDS: '0',
        ...env,
      },
    });
    const calls = existsSync(logPath)
      ? readFileSync(logPath, 'utf8').trim().split('\n').filter(Boolean).map((line) => JSON.parse(line))
      : [];
    return { status: result.status, stdout: result.stdout, stderr: result.stderr, calls };
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const blocked = (calls) => calls.some((c) => c.kind === 'issue-edit' && c.args.includes('agent-blocked'));
const dispatches = (calls, workflow) => calls.filter((c) => c.kind === 'dispatch' && c.workflow === workflow);

describe('Copilot pull request handoff to the Claude architecture check', () => {
  it('dispatches the trusted architecture check for the exact head of an eligible Copilot PR', () => {
    const { status, stderr, calls } = run(handoffShell, { state: fixture() });
    assert.equal(status, 0, stderr);
    const [dispatch] = dispatches(calls, 'agent-copilot-architecture.yml');
    assert.deepEqual(dispatch.args, [
      'workflow', 'run', 'agent-copilot-architecture.yml', '--repo', REPO, '--ref', 'main',
      '-f', `issue_number=${ISSUE}`, '-f', `pr_number=${PR}`, '-f', `head_sha=${SHA}`,
    ]);
  });

  it('automatically marks a validated Copilot draft ready and dispatches the exact head', () => {
    const { status, stderr, calls } = run(handoffShell, {
      state: fixture({ pr: { isDraft: true } }),
      env: {
        GITHUB_EVENT_NAME: 'workflow_run',
        RUN_PR_NUMBER: PR,
        RUN_HEAD_SHA: SHA,
        RUN_EVENT: 'pull_request',
        RUN_CONCLUSION: 'success',
        RUN_PATH: '.github/workflows/validate.yml',
      },
    });
    assert.equal(status, 0, stderr);
    const [ready] = calls.filter((c) => c.kind === 'pr-ready');
    assert.ok(ready);
    assert.equal(ready.token, 'owner-copilot-token', 'Draft -> Ready must use the owner-scoped token');
    const [dispatch] = dispatches(calls, 'agent-copilot-architecture.yml');
    assert.equal(dispatch.token, 'workflow-token', 'architecture dispatch must stay on GITHUB_TOKEN');
    assert.ok(dispatch);
    assert.ok(dispatch.args.includes(`head_sha=${SHA}`));
  });

  it('fails closed when the owner token needed for Draft -> Ready is unavailable', () => {
    const { status, stderr, calls } = run(handoffShell, {
      state: fixture({ pr: { isDraft: true } }),
      env: {
        GITHUB_EVENT_NAME: 'workflow_run',
        RUN_PR_NUMBER: PR,
        RUN_HEAD_SHA: SHA,
        RUN_EVENT: 'pull_request',
        RUN_CONCLUSION: 'success',
        RUN_PATH: '.github/workflows/validate.yml',
        COPILOT_AGENT_TOKEN: '',
      },
    });
    assert.notEqual(status, 0);
    assert.match(stderr, /COPILOT_AGENT_TOKEN is not configured/);
    assert.equal(calls.filter((c) => c.kind === 'pr-ready').length, 0);
    assert.equal(dispatches(calls, 'agent-copilot-architecture.yml').length, 0);
  });

  it('does not auto-ready when validation or the Copilot cloud run is not successful', () => {
    for (const state of [
      fixture({ statuses: { [SHA]: [{ context: 'merge-validation', state: 'failure' }] }, pr: { isDraft: true } }),
      fixture({ runs: [copilotRun({ status: 'in_progress', conclusion: null })], pr: { isDraft: true } }),
      fixture({ runs: [copilotRun({ status: 'completed', conclusion: 'failure' })], pr: { isDraft: true } }),
      // A newer @copilot follow-up session is still pushing, even though the initial run finished.
      fixture({
        runs: [
          copilotRun({ status: 'completed', conclusion: 'success' }),
          copilotRun({ name: `Addressing comment on PR #${PR}`, status: 'in_progress', conclusion: null, created_at: '2026-10-02T12:30:00Z' }),
        ],
        pr: { isDraft: true },
      }),
      // No Copilot session on this PR's branch.
      fixture({ runs: [copilotRun({ status: 'completed', conclusion: 'success', head_branch: 'copilot/other' })], pr: { isDraft: true } }),
    ]) {
      const { status, calls } = run(handoffShell, {
        state,
        env: {
          GITHUB_EVENT_NAME: 'workflow_run',
          RUN_PR_NUMBER: PR,
          RUN_HEAD_SHA: SHA,
          RUN_EVENT: 'pull_request',
          RUN_CONCLUSION: 'success',
          RUN_PATH: '.github/workflows/validate.yml',
        },
      });
      assert.equal(status, 0);
      assert.equal(calls.filter((c) => c.kind === 'pr-ready').length, 0);
      assert.equal(dispatches(calls, 'agent-copilot-architecture.yml').length, 0);
    }
  });

  for (const [name, overrides] of [
    ['a Claude agent branch', { pr: { headRefName: `agent/issue-${ISSUE}-x` } }],
    ['a copilot/* branch not authored by Copilot', { author: 'cristhyanc' }],
    ['a draft', { pr: { isDraft: true } }],
    ['a fork', { pr: { headRepositoryOwner: { login: 'someone' } } }],
    ['a stale head', { pr: { headRefOid: NEWER_SHA } }],
    ['an issue that already left agent-working', { issue: { labels: [{ name: 'agent-review' }] } }],
  ]) {
    it(`skips ${name} without dispatching`, () => {
      const { status, calls } = run(handoffShell, { state: fixture(overrides) });
      assert.equal(status, 0);
      assert.equal(calls.length, 0);
    });
  }

  for (const [name, overrides] of [
    ['changes workflows', { files: ['.github/workflows/validate.yml'] }],
    ['closes no issue', { pr: { closingIssuesReferences: [] } }],
    ['closes two issues', { pr: { closingIssuesReferences: [{ number: 281 }, { number: 282 }] } }],
  ]) {
    it(`fails without dispatching when the Copilot PR ${name}`, () => {
      const { status, calls } = run(handoffShell, { state: fixture(overrides) });
      assert.notEqual(status, 0);
      assert.equal(calls.length, 0);
    });
  }
});

describe('Claude architecture check finalizer', () => {
  const finding = { path: 'backend/Api/ProductsController.cs', line: 12, rule: 'Controllers delegate to use cases', problem: 'Controller queries the DbContext directly.', fix: 'Move the query into ListProducts.' };
  const output = (overrides = {}) => JSON.stringify({ checked_head_sha: SHA, verdict: 'CLEAN', findings: [], ...overrides });
  const env = (overrides = {}) => ({
    ISSUE_NUMBER: ISSUE,
    CONTEXT_JOB_RESULT: 'success',
    CHECK_JOB_RESULT: 'success',
    CHECK_OUTPUT: output(),
    COPILOT_AGENT_TOKEN: 'owner-copilot-token',
    ...overrides,
  });

  it('labels for review and dispatches exact-SHA validation when Claude finds nothing', () => {
    const { status, stderr, calls } = run(finalizeShell, { state: fixture(), env: env() });
    assert.equal(status, 0, stderr);
    const [dispatch] = dispatches(calls, 'validate.yml');
    assert.ok(dispatch.args.includes(`head_sha=${SHA}`) && dispatch.args.includes('dispatch_review=true') && dispatch.args.includes('main'));
    assert.ok(calls.some((c) => c.kind === 'pr-edit' && c.args.includes('agent-review')));
    assert.ok(calls.some((c) => c.kind === 'issue-edit' && c.args.includes('agent-working') && c.args.includes('agent-review')));
    assert.ok(!blocked(calls));
  });

  it('asks Copilot to fix findings with the owner token, does not validate the unfixed head, and never edits code itself', () => {
    const { status, stderr, calls } = run(finalizeShell, { state: fixture(), env: env({ CHECK_OUTPUT: output({ verdict: 'FINDINGS', findings: [finding] }) }) });
    assert.equal(status, 0, stderr);
    const request = calls.find((c) => c.kind === 'pr-comment');
    assert.equal(request.token, 'owner-copilot-token', 'Copilot only acts on mentions from a person with write access');
    assert.match(request.body, /^@copilot /);
    assert.match(request.body, /backend\/Api\/ProductsController\.cs:12/);
    assert.equal(dispatches(calls, 'validate.yml').length, 0, 'validation follows Copilot\'s fix push via agent-head-update');
    assert.ok(calls.some((c) => c.kind === 'pr-edit' && c.args.includes('agent-architecture-fix')), 'the label lets the fix push be validated');
    assert.ok(!calls.some((c) => c.args?.includes('agent-review')), 'no review may be requested before the fix push passes validation');
    assert.ok(!blocked(calls));
  });

  const sonarIssue = '- `backend/Inventory.Domain/Purchases/PurchaseCostTransitionPolicy.cs:41` [csharpsquid:S1172] (CODE_SMELL, MAJOR): Remove this unused method parameter.';

  it('asks Copilot to fix SonarCloud issues even when Claude finds no architecture problem', () => {
    const { status, stderr, calls } = run(finalizeShell, { state: fixture(), env: env({ SONAR_STATUS: 'analysed', SONAR_COUNT: '1', SONAR_ISSUES: sonarIssue }) });
    assert.equal(status, 0, stderr);
    const request = calls.find((c) => c.kind === 'pr-comment');
    assert.equal(request.token, 'owner-copilot-token');
    assert.match(request.body, /^@copilot /);
    assert.match(request.body, /PurchaseCostTransitionPolicy\.cs:41/);
    assert.match(request.body, /Never silence a SonarCloud issue/);
    assert.equal(dispatches(calls, 'validate.yml').length, 0, 'the unfixed head is not validated');
    assert.ok(calls.some((c) => c.kind === 'pr-edit' && c.args.includes('agent-architecture-fix')));
    assert.ok(!calls.some((c) => c.args?.includes('agent-review')));
    assert.ok(!blocked(calls));
  });

  it('treats an unavailable or malformed SonarCloud read as no issues and still validates a clean head', () => {
    for (const sonar of [{ SONAR_STATUS: 'unavailable', SONAR_COUNT: '0', SONAR_ISSUES: 'timeout' }, { SONAR_COUNT: '' }, { SONAR_COUNT: 'many' }]) {
      const { status, stderr, calls } = run(finalizeShell, { state: fixture(), env: env(sonar) });
      assert.equal(status, 0, stderr);
      assert.equal(dispatches(calls, 'validate.yml').length, 1);
      assert.ok(!calls.some((c) => c.kind === 'pr-comment' && /^@copilot/.test(c.body ?? '')));
      assert.ok(!blocked(calls));
    }
  });

  for (const [name, overrides] of [
    ['a rejected target check', { CONTEXT_JOB_RESULT: 'failure', CHECK_JOB_RESULT: 'skipped', CHECK_OUTPUT: '' }],
    ['a failed Claude check', { CHECK_JOB_RESULT: 'failure', CHECK_OUTPUT: '' }],
    ['output bound to another SHA', { CHECK_OUTPUT: output({ checked_head_sha: NEWER_SHA }) }],
    ['a CLEAN verdict that lists findings', { CHECK_OUTPUT: output({ findings: [finding] }) }],
    ['a FINDINGS verdict without findings', { CHECK_OUTPUT: output({ verdict: 'FINDINGS' }) }],
    ['findings but no Copilot token', { CHECK_OUTPUT: output({ verdict: 'FINDINGS', findings: [finding] }), COPILOT_AGENT_TOKEN: '' }],
  ]) {
    it(`blocks the agent-working issue on ${name}`, () => {
      const { status, calls } = run(finalizeShell, { state: fixture(), env: env(overrides) });
      assert.notEqual(status, 0);
      assert.ok(blocked(calls));
      assert.equal(dispatches(calls, 'validate.yml').length, 0);
      assert.ok(!calls.some((c) => c.kind === 'pr-comment' && /^@copilot/.test(c.body ?? '')));
    });
  }

  it('blocks when the Copilot PR head moved during the check', () => {
    const { status, calls } = run(finalizeShell, { state: fixture({ pr: { headRefOid: NEWER_SHA } }), env: env() });
    assert.notEqual(status, 0);
    assert.ok(blocked(calls));
  });

  it('never blocks an issue that is not agent-working', () => {
    const { status, calls } = run(finalizeShell, { state: fixture({ issue: { labels: [{ name: 'agent-review' }] } }), env: env({ CHECK_JOB_RESULT: 'failure' }) });
    assert.notEqual(status, 0);
    assert.equal(calls.length, 0);
  });
});
