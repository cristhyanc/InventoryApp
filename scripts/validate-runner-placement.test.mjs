// Deterministic tests for the runner placement contract (issue #645).
// Run with: node --test scripts/validate-runner-placement.test.mjs
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, it } from 'node:test';

import {
  ISOLATION_CONDITION,
  ISOLATION_GATE_STEP,
  SELF_HOSTED_RUNS_ON,
  SETUP_NODE_STEP,
  listWorkflowFiles,
  runnerPlacementViolations,
  verifyRunnerPlacement,
  workflowJobs,
} from './validate-runner-placement.mjs';

const workflow = (jobs) => `name: Fixture\non:\n  workflow_dispatch:\n\npermissions: {}\n\njobs:\n${jobs}`;
const job = (id, runsOn) => `  ${id}:\n    runs-on: ${runsOn}\n    steps:\n      - run: echo ok\n`;

describe('runner placement contract', () => {
  it('accepts the repository workflows, including deployment and reusable workflows', () => {
    const paths = listWorkflowFiles();
    assert.ok(paths.includes('.github/workflows/deploy-production.yml'));
    assert.ok(paths.includes('.github/workflows/agent-model-selection.yml'));
    assert.ok(paths.includes('.github/workflows/vm-manager.yml'));
    verifyRunnerPlacement({ read: (path) => readFileSync(resolve(path), 'utf8'), paths });
  });

  it('accepts a job on the self-hosted labels and a local reusable workflow call', () => {
    const text = workflow(`${job('build', SELF_HOSTED_RUNS_ON)}  model:\n    uses: ./.github/workflows/agent-model-selection.yml\n    with:\n      issue: 1\n`);
    assert.deepEqual(runnerPlacementViolations(text, 'fixture'), []);
  });

  it('rejects GitHub-hosted runner labels', () => {
    for (const label of ['ubuntu-latest', 'ubuntu-24.04', 'windows-latest', 'macos-14', '[ubuntu-latest]']) {
      const [violation] = runnerPlacementViolations(workflow(job('build', label)), 'fixture');
      assert.match(violation, /selects a GitHub-hosted runner/, label);
    }
  });

  it('rejects the retired opt-in switch and any computed runner selection', () => {
    const fallback = "${{ vars.INVENTORY_AGENT_SELF_HOSTED_ALL == 'true' && fromJSON('[\"self-hosted\",\"linux\",\"x64\",\"inventory-agent\"]') || 'ubuntu-latest' }}";
    const violations = runnerPlacementViolations(workflow(job('build', fallback)), 'fixture');
    assert.ok(violations.some((v) => /retired INVENTORY_AGENT_SELF_HOSTED_ALL/.test(v)));
    assert.ok(violations.some((v) => /GitHub-hosted runner/.test(v)));
    const computed = runnerPlacementViolations(workflow(job('build', '${{ vars.RUNNER }}')), 'fixture');
    assert.match(computed[0], /with an expression/);
  });

  it('rejects partial, extra, group and missing runner selections', () => {
    for (const runsOn of ['self-hosted', '[self-hosted, linux, x64]', '[self-hosted, linux, x64, inventory-agent, gpu]', '[self-hosted, linux, arm64, inventory-agent]']) {
      assert.match(runnerPlacementViolations(workflow(job('build', runsOn)), 'fixture')[0], /must be exactly/, runsOn);
    }
    const group = workflow('  build:\n    runs-on:\n      group: larger-runners\n    steps:\n      - run: echo ok\n');
    assert.match(runnerPlacementViolations(group, 'fixture')[0], /must be exactly/);
    const missing = workflow('  build:\n    steps:\n      - run: echo ok\n');
    assert.match(runnerPlacementViolations(missing, 'fixture')[0], /exactly one runs-on/);
  });

  it('checks every job, not only the first', () => {
    const text = workflow(`${job('first', SELF_HOSTED_RUNS_ON)}\n${job('deploy', 'ubuntu-latest')}`);
    const violations = runnerPlacementViolations(text, 'fixture');
    assert.equal(violations.length, 1);
    assert.match(violations[0], /job 'deploy'/);
  });

  it('rejects a reusable workflow outside the repository', () => {
    const text = workflow('  shared:\n    uses: octo/shared/.github/workflows/build.yml@v1\n');
    assert.match(runnerPlacementViolations(text, 'fixture')[0], /only a reusable workflow in this repository/);
  });

  it('ignores step-level uses: and nested runs-on text', () => {
    const text = workflow(`  build:\n    runs-on: ${SELF_HOSTED_RUNS_ON}\n    steps:\n      - uses: actions/checkout@v4\n      - name: Mention\n        run: echo "runs-on: ubuntu-latest is not used"\n`);
    assert.deepEqual(runnerPlacementViolations(text, 'fixture'), []);
  });

  it('requires the pinned Node.js setup before a job runs node', () => {
    const runs = (steps) => workflow(`  check:\n    runs-on: ${SELF_HOSTED_RUNS_ON}\n    steps:\n${steps}`);
    const call = '      - run: node "$RUNNER_TEMP/agent-pr-guard.mjs" check 1\n';
    const setup = `      - name: Set up Node.js\n        ${SETUP_NODE_STEP}\n        with:\n          node-version: 20\n`;
    assert.match(runnerPlacementViolations(runs(call), 'fixture')[0], /runs node without first installing it/);
    assert.match(runnerPlacementViolations(runs(call + setup), 'fixture')[0], /runs node without first installing it/);
    assert.deepEqual(runnerPlacementViolations(runs(setup + call), 'fixture'), []);
    assert.deepEqual(runnerPlacementViolations(runs('      - run: echo "node-version is set elsewhere"\n'), 'fixture'), []);
  });

  it('requires the isolation condition on every job that receives a secret or OIDC token', () => {
    const credentialed = (condition, body = "        env:\n          TOKEN: ${{ secrets.CLAUDE_CODE_OAUTH_TOKEN }}\n") =>
      workflow(`  agent:\n${condition}    runs-on: ${SELF_HOSTED_RUNS_ON}\n    steps:\n      - run: echo ok\n${body}`);
    const rejected = /receives a secret or OIDC token/;
    assert.match(runnerPlacementViolations(credentialed(''), 'fixture')[0], rejected);
    assert.match(runnerPlacementViolations(credentialed("    if: github.ref == 'refs/heads/main'\n"), 'fixture')[0], rejected);
    // `&&` binds tighter than `||`, so a trailing condition after a top-level `||` can be bypassed.
    assert.match(runnerPlacementViolations(credentialed(`    if: github.event_name == 'push' || github.ref == 'refs/heads/main' && ${ISOLATION_CONDITION}\n`), 'fixture')[0], rejected);
    assert.match(runnerPlacementViolations(credentialed(`    if: ${ISOLATION_CONDITION} || always()\n`), 'fixture')[0], rejected);
    assert.match(runnerPlacementViolations(credentialed("    if: vars.INVENTORY_RUNNER_ISOLATION_VERIFIED != 'true'\n"), 'fixture')[0], rejected);
    for (const body of ['    permissions:\n      id-token: write\n', '    secrets:\n      TOKEN: ${{ secrets.TOKEN }}\n']) {
      const text = workflow(`  agent:\n    permissions:\n      contents: read\n${body.startsWith('    permissions') ? '      id-token: write\n' : ''}    runs-on: ${SELF_HOSTED_RUNS_ON}\n${body.startsWith('    secrets') ? body : ''}    steps:\n      - run: echo ok\n`);
      assert.match(runnerPlacementViolations(text, 'fixture')[0], rejected, body);
    }
    for (const condition of [
      `    if: ${ISOLATION_CONDITION}\n`,
      `    if: (github.event_name == 'push' || github.ref == 'refs/heads/main') && ${ISOLATION_CONDITION}\n`,
      `    if: \${{ !cancelled() && ${ISOLATION_CONDITION} }}\n`,
      `    if: |\n      ${ISOLATION_CONDITION} &&\n      github.event.issue.pull_request != null\n`,
    ]) {
      assert.deepEqual(runnerPlacementViolations(credentialed(condition), 'fixture'), [], condition);
    }
    // The job's own GITHUB_TOKEN expires with the job and needs no gate.
    assert.deepEqual(runnerPlacementViolations(credentialed('', "        env:\n          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}\n"), 'fixture'), []);
  });

  it('requires a visible isolation refusal in the workflows a person starts', () => {
    const path = '.github/workflows/deploy-production.yml';
    const text = readFileSync(resolve(path), 'utf8');
    assert.deepEqual(runnerPlacementViolations(text, path), []);
    const silent = text.replaceAll(`      - name: ${ISOLATION_GATE_STEP}\n`, '      - name: Something else\n');
    assert.match(runnerPlacementViolations(silent, path).at(-1), /refused credentialed job would be skipped silently/);
  });

  it('fails closed on a workflow it cannot read', () => {
    assert.throws(() => workflowJobs('name: x\non: push\n', 'fixture'), /no top-level jobs/);
    assert.throws(() => workflowJobs('jobs:\n\tbuild:\n', 'fixture'), /cannot read the jobs map/);
    assert.throws(() => verifyRunnerPlacement({ read: () => '', paths: [] }), /no workflow files/);
  });
});
