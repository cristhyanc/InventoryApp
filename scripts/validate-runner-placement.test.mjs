// Deterministic tests for the runner placement contract (issues #645 and #807).
// Run with: node --test scripts/validate-runner-placement.test.mjs
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, it } from 'node:test';

import {
  BASELINE_CONDITION,
  GITHUB_HOSTED_LABEL,
  ISOLATION_CONDITION,
  ISOLATION_GATE_STEP,
  ISOLATION_REFUSAL_CONDITION,
  ISOLATION_REFUSAL_STEP_TEST,
  RUNNER_MODE_ENV,
  RUNNER_MODE_STEP,
  RUNS_ON_SELECTOR,
  SELF_HOSTED_LABELS,
  SETUP_NODE_STEP,
  listWorkflowFiles,
  runnerPlacementViolations,
  verifyRunnerPlacement,
  workflowJobs,
} from './validate-runner-placement.mjs';

const workflow = (jobs) => `name: Fixture\non:\n  workflow_dispatch:\n\npermissions: {}\n\njobs:\n${jobs}`;
const job = (id, runsOn = RUNS_ON_SELECTOR, steps = '      - run: echo ok\n') => `  ${id}:\n    runs-on: ${runsOn}\n    steps:\n${RUNNER_MODE_STEP}${steps}`;
const credentialedBody = "        env:\n          TOKEN: ${{ secrets.CLAUDE_CODE_OAUTH_TOKEN }}\n";
const credentialed = (condition, body = credentialedBody) =>
  workflow(`  agent:\n${condition}    runs-on: ${RUNS_ON_SELECTOR}\n    steps:\n${RUNNER_MODE_STEP}      - run: echo ok\n${body}`);
const ISOLATION_REJECTED = /receives a secret, an OIDC token or a write-capable GITHUB_TOKEN/;

/**
 * A small evaluator for the subset of the GitHub Actions expression language the approved texts use:
 * `vars.NAME`, string literals, `fromJSON('...')`, `==`, `!=`, `!`, `&&`, `||` and parentheses.
 * Like GitHub it compares strings without regard to case, treats an unset variable as the empty
 * string, and returns an operand (not a boolean) from `&&` and `||`. It models GitHub's documented
 * behaviour so the truth tables below can be checked; it is not GitHub's evaluator.
 */
function evaluate(expression, vars) {
  const source = expression.trim().replace(/^\$\{\{([\s\S]*)\}\}$/, '$1');
  const pattern = /\s*('(?:[^']|'')*'|==|!=|&&|\|\||[()!]|[A-Za-z_][\w.-]*)/y;
  const tokens = [];
  let consumed = 0;
  for (let match = pattern.exec(source); match; match = pattern.exec(source)) {
    tokens.push(match[1]);
    consumed = pattern.lastIndex;
  }
  assert.equal(source.slice(consumed).trim(), '', `cannot tokenize: ${expression}`);
  let position = 0;
  const peek = () => tokens[position];
  const take = (expected) => {
    const token = tokens[position++];
    if (expected !== undefined) assert.equal(token, expected, `expected ${expected} in ${expression}`);
    return token;
  };
  const truthy = (value) => value !== '' && value !== null && value !== false && value !== 0;
  const equal = (left, right) =>
    typeof left === 'string' && typeof right === 'string' ? left.toLowerCase() === right.toLowerCase() : left === right;
  function primary() {
    const token = take();
    if (token === '(') {
      const value = or();
      take(')');
      return value;
    }
    if (token === '!') return !truthy(primary());
    if (token.startsWith("'")) return token.slice(1, -1).replaceAll("''", "'");
    if (token === 'fromJSON') {
      take('(');
      const literal = primary();
      take(')');
      return JSON.parse(literal);
    }
    const variable = /^vars\.(\w+)$/.exec(token);
    assert.ok(variable, `unsupported token ${token} in ${expression}`);
    return vars[variable[1]] ?? '';
  }
  function comparison() {
    let left = primary();
    while (peek() === '==' || peek() === '!=') {
      const operator = take();
      const right = primary();
      left = operator === '==' ? equal(left, right) : !equal(left, right);
    }
    return left;
  }
  function and() {
    let left = comparison();
    while (peek() === '&&') {
      take();
      const right = comparison();
      left = truthy(left) ? right : left;
    }
    return left;
  }
  function or() {
    let left = and();
    while (peek() === '||') {
      take();
      const right = and();
      left = truthy(left) ? left : right;
    }
    return left;
  }
  const value = or();
  assert.equal(position, tokens.length, `trailing tokens in ${expression}`);
  return value;
}

/** Runs the approved runner mode step's script in bash, as the runner would. */
function runModeStep(mode, runnerKind) {
  const script = RUNNER_MODE_STEP.split('        run: |\n')[1].replace(/^ {10}/gm, '');
  const env = { PATH: process.env.PATH, RUNNER_NAME: 'fixture-runner', RUNNER_KIND: runnerKind };
  if (mode !== undefined) env.RUNNER_MODE = mode;
  return spawnSync('bash', ['-e', '-c', script], { env, encoding: 'utf8' });
}

const MODES = [
  { mode: 'true', valid: true, selfHosted: true },
  { mode: 'false', valid: true, selfHosted: false },
  { mode: undefined, valid: true, selfHosted: false },
  { mode: '', valid: true, selfHosted: false },
  { mode: 'TRUE', valid: false, selfHosted: true },
  { mode: 'True', valid: false, selfHosted: true },
  { mode: 'yes', valid: false, selfHosted: false },
  { mode: ' true', valid: false, selfHosted: false },
  { mode: '1', valid: false, selfHosted: false },
];
const VERIFIED = [undefined, '', 'false', 'true', 'TRUE'];
const varsFor = (mode, isolation, baseline) => ({
  INVENTORY_AGENT_SELF_HOSTED_ALL: mode,
  INVENTORY_RUNNER_ISOLATION_VERIFIED: isolation,
  INVENTORY_RUNNER_BASELINE_VERIFIED: baseline,
});
const verified = (value) => value?.toLowerCase() === 'true';

describe('runner placement contract', () => {
  it('accepts the repository workflows, including deployment and reusable workflows', () => {
    const paths = listWorkflowFiles();
    assert.ok(paths.includes('.github/workflows/deploy-production.yml'));
    assert.ok(paths.includes('.github/workflows/deploy-production-recovery.yml'));
    assert.ok(paths.includes('.github/workflows/deploy-production-release.yml'));
    assert.ok(paths.includes('.github/workflows/agent-model-selection.yml'));
    assert.ok(paths.includes('.github/workflows/vm-manager.yml'));
    verifyRunnerPlacement({ read: (path) => readFileSync(resolve(path), 'utf8'), paths });
  });

  it('selects the self-hosted labels only for true, and ubuntu-latest for false or unset', () => {
    for (const { mode, selfHosted } of MODES) {
      const runner = evaluate(RUNS_ON_SELECTOR, varsFor(mode));
      assert.deepEqual(runner, selfHosted ? SELF_HOSTED_LABELS : GITHUB_HOSTED_LABEL, `mode ${JSON.stringify(mode)}`);
    }
  });

  it('lets a credentialed job start on a self-hosted runner only with verified isolation', () => {
    for (const { mode, selfHosted } of MODES) {
      for (const isolation of VERIFIED) {
        const vars = varsFor(mode, isolation);
        const starts = Boolean(evaluate(ISOLATION_CONDITION, vars));
        const where = `mode ${JSON.stringify(mode)}, isolation ${JSON.stringify(isolation)}`;
        // GitHub-hosted runners are fresh per job, so the owner's VM verification does not apply.
        assert.equal(starts, selfHosted ? verified(isolation) : true, where);
        // Whatever the value, no job reaches a self-hosted runner without verified isolation.
        if (Array.isArray(evaluate(RUNS_ON_SELECTOR, vars)) && starts) assert.ok(verified(isolation), where);
        // The visible refusal is the exact complement of the gate, so a refused run never passes silently.
        assert.equal(Boolean(evaluate(ISOLATION_REFUSAL_CONDITION, vars)), !starts, where);
      }
    }
  });

  it('requires the verified baseline for deployment only on self-hosted runners', () => {
    for (const { mode, selfHosted } of MODES) {
      for (const baseline of VERIFIED) {
        const starts = Boolean(evaluate(BASELINE_CONDITION, varsFor(mode, 'true', baseline)));
        assert.equal(starts, selfHosted ? verified(baseline) : true, `mode ${JSON.stringify(mode)}, baseline ${JSON.stringify(baseline)}`);
      }
    }
  });

  it('runner mode step accepts exactly true, false or unset and reports the runner', () => {
    for (const { mode, valid, selfHosted } of MODES) {
      const kind = selfHosted ? 'self-hosted' : 'github-hosted';
      const result = runModeStep(mode, kind);
      const where = `mode ${JSON.stringify(mode)}`;
      assert.equal(result.status === 0, valid, `${where}: ${result.stdout}`);
      if (valid) {
        assert.match(result.stdout, new RegExp(`selects ${kind} runners`), where);
        assert.match(result.stdout, new RegExp(`runs on fixture-runner \\(${kind}\\)`), where);
      } else {
        assert.match(result.stdout, /must be exactly true, false or unset/, where);
      }
    }
  });

  it('runner mode step fails a job on a different kind of runner than the mode selects', () => {
    for (const [mode, kind] of [['true', 'github-hosted'], ['false', 'self-hosted'], [undefined, 'self-hosted'], ['true', '']]) {
      const result = runModeStep(mode, kind);
      assert.notEqual(result.status, 0, `${mode} on ${kind}`);
      assert.match(result.stdout, /the variable changed after the job was queued/);
    }
  });

  it('accepts the approved selector with the mode step and a local reusable workflow call', () => {
    const text = workflow(`${job('build')}  model:\n    uses: ./.github/workflows/agent-model-selection.yml\n    with:\n      issue: 1\n`);
    assert.deepEqual(runnerPlacementViolations(text, 'fixture'), []);
  });

  it('rejects fixed runners, other expressions and fallbacks', () => {
    for (const runsOn of [
      'ubuntu-latest',
      'ubuntu-24.04',
      'windows-latest',
      'macos-14',
      '[ubuntu-latest]',
      '[self-hosted, linux, x64, inventory-agent]',
      'self-hosted',
      '[self-hosted, linux, x64, inventory-agent, gpu]',
      '${{ vars.RUNNER }}',
      "${{ vars.INVENTORY_AGENT_SELF_HOSTED_ALL == 'true' && fromJSON('[\"self-hosted\",\"linux\",\"x64\",\"inventory-agent\"]') || 'ubuntu-22.04' }}",
      "${{ vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'false' && fromJSON('[\"self-hosted\",\"linux\",\"x64\",\"inventory-agent\"]') || 'ubuntu-latest' }}",
      "${{ vars.INVENTORY_AGENT_SELF_HOSTED_ALL == 'true' && fromJSON('[\"self-hosted\",\"linux\",\"x64\",\"inventory-agent\"]') || fromJSON('[\"self-hosted\"]') }}",
      `${RUNS_ON_SELECTOR} # with a comment`,
    ]) {
      const violations = runnerPlacementViolations(workflow(job('build', runsOn)), 'fixture');
      assert.ok(violations.some((v) => /runs-on must be exactly the approved runner mode selector/.test(v)), runsOn);
    }
    const group = workflow(`  build:\n    runs-on:\n      group: larger-runners\n    steps:\n${RUNNER_MODE_STEP}      - run: echo ok\n`);
    assert.match(runnerPlacementViolations(group, 'fixture')[0], /must be exactly/);
    const missing = workflow(`  build:\n    steps:\n${RUNNER_MODE_STEP}      - run: echo ok\n`);
    assert.match(runnerPlacementViolations(missing, 'fixture')[0], /exactly one runs-on/);
  });

  it('rejects a mode that differs between jobs of one workflow', () => {
    const text = workflow(`${job('first')}\n${job('deploy', '[self-hosted, linux, x64, inventory-agent]')}`);
    const violations = runnerPlacementViolations(text, 'fixture');
    assert.equal(violations.length, 1);
    assert.match(violations[0], /job 'deploy'/);
  });

  it('requires the exact runner mode step as every job\'s first step', () => {
    const rejected = /first step must be exactly the approved 'Report and validate runner mode' step/;
    const without = workflow(`  build:\n    runs-on: ${RUNS_ON_SELECTOR}\n    steps:\n      - run: echo ok\n`);
    assert.match(runnerPlacementViolations(without, 'fixture')[0], rejected);
    const later = workflow(`  build:\n    runs-on: ${RUNS_ON_SELECTOR}\n    steps:\n      - run: echo first\n${RUNNER_MODE_STEP}`);
    assert.match(runnerPlacementViolations(later, 'fixture')[0], rejected);
    const lenient = workflow(job('build').replace("''|false) selected=github-hosted ;;\n            *)", "''|false) selected=github-hosted ;;\n            True|TRUE) selected=self-hosted ;;\n            *)"));
    assert.match(runnerPlacementViolations(lenient, 'fixture')[0], rejected);
    const unchecked = workflow(job('build').replace('          if [ "$RUNNER_KIND" != "$selected" ]; then', '          if false; then'));
    assert.match(runnerPlacementViolations(unchecked, 'fixture')[0], rejected);
  });

  it('rejects any other reading of the switch', () => {
    const rejected = /reads INVENTORY_AGENT_SELF_HOSTED_ALL outside the approved selector/;
    for (const condition of [
      "    if: vars.INVENTORY_AGENT_SELF_HOSTED_ALL == 'false'\n",
      "    if: vars.inventory_agent_self_hosted_all != 'true'\n",
      "    if: vars['INVENTORY_AGENT_SELF_HOSTED_ALL'] == 'true'\n",
    ]) {
      const text = workflow(`  build:\n${condition}    runs-on: ${RUNS_ON_SELECTOR}\n    steps:\n${RUNNER_MODE_STEP}      - run: echo ok\n`);
      assert.ok(runnerPlacementViolations(text, 'fixture').some((v) => rejected.test(v)), condition);
    }
    const stepEnv = workflow(job('build', RUNS_ON_SELECTOR, '      - env:\n          MODE: ${{ vars.INVENTORY_AGENT_SELF_HOSTED_ALL }}\n        run: echo "$MODE"\n'));
    assert.match(runnerPlacementViolations(stepEnv, 'fixture')[0], rejected);
    // Comments and messages that name the variable are not readings of it.
    const comment = workflow(job('build', RUNS_ON_SELECTOR, '      # vars.INVENTORY_AGENT_SELF_HOSTED_ALL selects the runner.\n      - run: echo "Set INVENTORY_AGENT_SELF_HOSTED_ALL to true."\n'));
    assert.deepEqual(runnerPlacementViolations(comment, 'fixture'), []);
  });

  it('rejects a reusable workflow outside the repository', () => {
    const text = workflow('  shared:\n    uses: octo/shared/.github/workflows/build.yml@v1\n');
    assert.match(runnerPlacementViolations(text, 'fixture')[0], /only a reusable workflow in this repository/);
  });

  it('ignores step-level uses: and nested runs-on text', () => {
    const text = workflow(job('build', RUNS_ON_SELECTOR, '      - uses: actions/checkout@v4\n      - name: Mention\n        run: echo "runs-on: ubuntu-latest is not used"\n'));
    assert.deepEqual(runnerPlacementViolations(text, 'fixture'), []);
  });

  it('requires the pinned Node.js setup before a job runs node', () => {
    const runs = (steps) => workflow(job('check', RUNS_ON_SELECTOR, steps));
    const call = '      - run: node "$RUNNER_TEMP/agent-pr-guard.mjs" check 1\n';
    const setup = `      - name: Set up Node.js\n        ${SETUP_NODE_STEP}\n        with:\n          node-version: 20\n`;
    assert.match(runnerPlacementViolations(runs(call), 'fixture')[0], /runs node without first installing it/);
    assert.match(runnerPlacementViolations(runs(call + setup), 'fixture')[0], /runs node without first installing it/);
    assert.deepEqual(runnerPlacementViolations(runs(setup + call), 'fixture'), []);
    assert.deepEqual(runnerPlacementViolations(runs('      - run: echo "node-version is set elsewhere"\n'), 'fixture'), []);
  });

  it('requires the conditional isolation gate on every job that receives a secret or OIDC token', () => {
    const plainIsolation = "vars.INVENTORY_RUNNER_ISOLATION_VERIFIED == 'true'";
    for (const condition of [
      '',
      "    if: github.ref == 'refs/heads/main'\n",
      // `&&` binds tighter than `||`, so a trailing condition after a top-level `||` can be bypassed.
      `    if: github.event_name == 'push' || github.ref == 'refs/heads/main' && ${ISOLATION_CONDITION}\n`,
      `    if: ${ISOLATION_CONDITION} || always()\n`,
      // Without its parentheses the gate's own `||` lets a self-hosted job skip verification.
      "    if: vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'true' || vars.INVENTORY_RUNNER_ISOLATION_VERIFIED == 'true'\n",
      "    if: github.ref == 'refs/heads/main' && vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'true' || vars.INVENTORY_RUNNER_ISOLATION_VERIFIED == 'true'\n",
      // A gate that skips verification in self-hosted mode, or ignores isolation altogether.
      "    if: (vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'false' || vars.INVENTORY_RUNNER_ISOLATION_VERIFIED == 'true')\n",
      "    if: (vars.INVENTORY_AGENT_SELF_HOSTED_ALL == 'true' || vars.INVENTORY_RUNNER_ISOLATION_VERIFIED == 'true')\n",
      "    if: (vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'true' || vars.INVENTORY_RUNNER_ISOLATION_VERIFIED != 'false')\n",
      `    if: ${ISOLATION_REFUSAL_CONDITION}\n`,
      // The #645 gate alone is no longer the approved text: it would refuse GitHub-hosted runs.
      `    if: ${plainIsolation}\n`,
    ]) {
      assert.ok(runnerPlacementViolations(credentialed(condition), 'fixture').some((v) => ISOLATION_REJECTED.test(v)), condition);
    }
    // Bracket and whole-context forms are references too; a mention outside an expression is not.
    for (const reference of ["${{ secrets['CLAUDE_CODE_OAUTH_TOKEN'] }}", '${{ secrets [ "TOKEN" ] }}', '${{ toJSON(secrets) }}']) {
      const body = `        env:\n          TOKEN: ${reference}\n`;
      assert.match(runnerPlacementViolations(credentialed('', body), 'fixture')[0], ISOLATION_REJECTED, reference);
    }
    assert.deepEqual(runnerPlacementViolations(credentialed('', '        # Uses no secrets.\n'), 'fixture'), []);
    for (const body of ['    permissions:\n      id-token: write\n', '    secrets:\n      TOKEN: ${{ secrets.TOKEN }}\n']) {
      const text = workflow(`  agent:\n    permissions:\n      contents: read\n${body.startsWith('    permissions') ? '      id-token: write\n' : ''}    runs-on: ${RUNS_ON_SELECTOR}\n${body.startsWith('    secrets') ? body : ''}    steps:\n${RUNNER_MODE_STEP}      - run: echo ok\n`);
      assert.match(runnerPlacementViolations(text, 'fixture')[0], ISOLATION_REJECTED, body);
    }
    for (const condition of [
      `    if: ${ISOLATION_CONDITION}\n`,
      `    if: (github.event_name == 'push' || github.ref == 'refs/heads/main') && ${ISOLATION_CONDITION}\n`,
      `    if: \${{ !cancelled() && ${ISOLATION_CONDITION} }}\n`,
      `    if: github.ref == 'refs/heads/main' && ${ISOLATION_CONDITION} && ${BASELINE_CONDITION}\n`,
      `    if: |\n      ${ISOLATION_CONDITION} &&\n      github.event.issue.pull_request != null\n`,
    ]) {
      assert.deepEqual(runnerPlacementViolations(credentialed(condition), 'fixture'), [], condition);
    }
    // A job whose GITHUB_TOKEN is read-only needs no gate, whichever way it names the token.
    for (const reference of ['${{ secrets.GITHUB_TOKEN }}', "${{ secrets['GITHUB_TOKEN'] }}", '${{ github.token }}']) {
      assert.deepEqual(runnerPlacementViolations(credentialed('', `        env:\n          GH_TOKEN: ${reference}\n`), 'fixture'), [], reference);
    }
  });

  it('treats a write-capable GITHUB_TOKEN as a credential, declared or inherited', () => {
    const rejected = /write-capable GITHUB_TOKEN/;
    const publisher = (permissions, condition = '') =>
      `  publish:\n${condition}    runs-on: ${RUNS_ON_SELECTOR}\n${permissions}    steps:\n${RUNNER_MODE_STEP}      - run: gh api --method POST repos/o/r/statuses/abc\n        env:\n          GH_TOKEN: \${{ github.token }}\n`;
    for (const scope of ['pull-requests', 'statuses', 'contents', 'actions', 'issues', 'deployments', 'checks']) {
      const permissions = `    permissions:\n      contents: read\n      ${scope}: write # publishes\n`;
      assert.match(runnerPlacementViolations(workflow(publisher(permissions)), 'fixture')[0], rejected, scope);
      assert.deepEqual(runnerPlacementViolations(workflow(publisher(permissions, `    if: always() && ${ISOLATION_CONDITION}\n`)), 'fixture'), [], scope);
    }
    assert.match(runnerPlacementViolations(workflow(publisher('    permissions: write-all\n')), 'fixture')[0], rejected);
    // A comment that mentions write is not a grant.
    assert.deepEqual(runnerPlacementViolations(workflow(publisher('    permissions:\n      # needs no write scope\n      contents: read\n')), 'fixture'), []);
    // A job without its own permissions inherits the workflow's, or the repository default when neither declares any.
    const inherited = (top) => `name: Fixture\non:\n  workflow_dispatch:\n\n${top}jobs:\n${publisher('')}`;
    assert.match(runnerPlacementViolations(inherited('permissions:\n  statuses: write\n\n'), 'fixture')[0], rejected);
    assert.match(runnerPlacementViolations(inherited(''), 'fixture')[0], rejected);
    assert.deepEqual(runnerPlacementViolations(inherited('permissions:\n  contents: read\n\n'), 'fixture'), []);
    assert.deepEqual(runnerPlacementViolations(inherited('permissions: {}\n\n'), 'fixture'), []);
    // A reusable-workflow caller with write scopes is gated like any other job.
    const caller = '  model:\n    uses: ./.github/workflows/agent-model-selection.yml\n    permissions:\n      actions: write\n';
    assert.match(runnerPlacementViolations(workflow(caller), 'fixture')[0], rejected);
    assert.deepEqual(runnerPlacementViolations(workflow(`  model:\n    if: ${ISOLATION_CONDITION}\n    uses: ./.github/workflows/agent-model-selection.yml\n    permissions:\n      actions: write\n`), 'fixture'), []);
  });

  it('rejects each repository writer once its isolation condition is removed', () => {
    const writers = [
      ['.github/workflows/agent-architecture.yml', `    if: always() && ${ISOLATION_CONDITION}\n`, '    if: always()\n'],
      ['.github/workflows/agent-review.yml', ` && ${ISOLATION_CONDITION}\n    needs:\n      - context\n      - review\n`, '\n    needs:\n      - context\n      - review\n'],
      ['.github/workflows/agent-head-update.yml', `      ${ISOLATION_CONDITION} &&\n`, ''],
      ['.github/workflows/agent-review-request.yml', `      ${ISOLATION_CONDITION} &&\n`, ''],
      ['.github/workflows/agent-branch-cleanup.yml', `'agent/issue-') && ${ISOLATION_CONDITION}\n`, "'agent/issue-')\n"],
      ['.github/workflows/agent-implement.yml', `dispatch_architecture == 'true' && ${ISOLATION_CONDITION}\n`, "dispatch_architecture == 'true'\n"],
      ['.github/workflows/agent-implement.yml', `    if: ${ISOLATION_CONDITION}\n    permissions:\n      contents: read\n      issues: read\n    uses:`, '    permissions:\n      contents: read\n      issues: read\n    uses:', /receives a secret/],
      ['.github/workflows/agent-model-selection.yml', `    if: ${ISOLATION_CONDITION}\n`, ''],
      ['.github/workflows/agent-repair.yml', `dispatch_validation == 'true' && ${ISOLATION_CONDITION}\n`, "dispatch_validation == 'true'\n"],
      ['.github/workflows/validate.yml', `publish_status == 'true' && ${ISOLATION_CONDITION}\n    needs: context\n`, "publish_status == 'true'\n    needs: context\n"],
      ['.github/workflows/validate.yml', `publish_status == 'true' && ${ISOLATION_CONDITION}\n    needs:\n`, "publish_status == 'true'\n    needs:\n"],
      ['.github/workflows/validate.yml', `needs.report-status.result == 'success' && ${ISOLATION_CONDITION}\n`, "needs.report-status.result == 'success'\n"],
      ['.github/workflows/deploy-production.yml', `    if: ${ISOLATION_CONDITION}\n`, ''],
      ['.github/workflows/deploy-production-recovery.yml', `    if: ${ISOLATION_CONDITION}\n`, ''],
      ['.github/workflows/deploy-production-release.yml', `    if: ${ISOLATION_CONDITION}\n`, ''],
    ];
    for (const [path, gated, ungated, expected = /write-capable GITHUB_TOKEN/] of writers) {
      const text = readFileSync(resolve(path), 'utf8');
      assert.equal(text.split(gated).length, 2, `${path}: fixture text must occur once: ${gated}`);
      const violations = runnerPlacementViolations(text.replace(gated, ungated), path);
      assert.equal(violations.length, 1, `${path}: ${violations.join('; ')}`);
      assert.match(violations[0], expected, path);
    }
  });

  it('rejects each deploy job once its self-hosted isolation or baseline condition is bypassed', () => {
    const path = '.github/workflows/deploy-production-release.yml';
    const text = readFileSync(resolve(path), 'utf8');
    const deployCondition = `    if: github.ref == 'refs/heads/main' && ${ISOLATION_CONDITION} && ${BASELINE_CONDITION}\n`;
    assert.equal(text.split(deployCondition).length, 3, 'Deploy API and Deploy Frontend carry the deploy condition');
    const bypass = deployCondition.replace(ISOLATION_CONDITION, "(vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'true' || vars.INVENTORY_RUNNER_ISOLATION_VERIFIED != 'true')");
    const violations = runnerPlacementViolations(text.replace(deployCondition, bypass), path);
    assert.ok(violations.some((v) => /job 'deploy-api'.*write-capable GITHUB_TOKEN/.test(v)), violations.join('; '));
    const baselineRead = deployCondition.replace(BASELINE_CONDITION, "(vars.INVENTORY_AGENT_SELF_HOSTED_ALL == 'false' || vars.INVENTORY_RUNNER_BASELINE_VERIFIED == 'true')");
    assert.ok(runnerPlacementViolations(text.replace(deployCondition, baselineRead), path).some((v) => /reads INVENTORY_AGENT_SELF_HOSTED_ALL outside/.test(v)));
  });

  it('requires a visible isolation refusal in the workflows a person starts', () => {
    const path = '.github/workflows/deploy-production.yml';
    const text = readFileSync(resolve(path), 'utf8');
    assert.deepEqual(runnerPlacementViolations(text, path), []);
    const silent = text.replaceAll(`      - name: ${ISOLATION_GATE_STEP}\n`, '      - name: Something else\n');
    assert.match(runnerPlacementViolations(silent, path).at(-1), /refused credentialed job would be skipped silently/);
  });

  it('rejects a refusal that would also block GitHub-hosted runs', () => {
    const rejected = /refuses on INVENTORY_RUNNER_ISOLATION_VERIFIED without the self-hosted mode condition/;
    const step = readFileSync(resolve('.github/workflows/deploy-production.yml'), 'utf8');
    assert.ok(step.includes(ISOLATION_REFUSAL_STEP_TEST));
    assert.ok(step.includes(RUNNER_MODE_ENV));
    const ignoresModeInStep = step.replace(ISOLATION_REFUSAL_STEP_TEST, 'if [ "$RUNNER_ISOLATION_VERIFIED" != "true" ]; then');
    assert.ok(runnerPlacementViolations(ignoresModeInStep, 'fixture').some((v) => rejected.test(v)));
    const job = readFileSync(resolve('.github/workflows/agent-branch-cleanup.yml'), 'utf8');
    assert.ok(job.includes(ISOLATION_REFUSAL_CONDITION));
    const ignoresModeInJob = job.replace(ISOLATION_REFUSAL_CONDITION, "vars.INVENTORY_RUNNER_ISOLATION_VERIFIED != 'true'");
    assert.ok(runnerPlacementViolations(ignoresModeInJob, 'fixture').some((v) => rejected.test(v)));
  });

  it('fails closed on a workflow it cannot read', () => {
    assert.throws(() => workflowJobs('name: x\non: push\n', 'fixture'), /no top-level jobs/);
    assert.throws(() => workflowJobs('jobs:\n\tbuild:\n', 'fixture'), /cannot read the jobs map/);
    assert.throws(() => verifyRunnerPlacement({ read: () => '', paths: [] }), /no workflow files/);
  });
});
