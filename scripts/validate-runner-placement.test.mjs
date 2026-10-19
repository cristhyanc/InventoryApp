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
    const rejected = /receives a secret, an OIDC token or a write-capable GITHUB_TOKEN/;
    assert.match(runnerPlacementViolations(credentialed(''), 'fixture')[0], rejected);
    assert.match(runnerPlacementViolations(credentialed("    if: github.ref == 'refs/heads/main'\n"), 'fixture')[0], rejected);
    // `&&` binds tighter than `||`, so a trailing condition after a top-level `||` can be bypassed.
    assert.match(runnerPlacementViolations(credentialed(`    if: github.event_name == 'push' || github.ref == 'refs/heads/main' && ${ISOLATION_CONDITION}\n`), 'fixture')[0], rejected);
    assert.match(runnerPlacementViolations(credentialed(`    if: ${ISOLATION_CONDITION} || always()\n`), 'fixture')[0], rejected);
    assert.match(runnerPlacementViolations(credentialed("    if: vars.INVENTORY_RUNNER_ISOLATION_VERIFIED != 'true'\n"), 'fixture')[0], rejected);
    // Bracket and whole-context forms are references too; a mention outside an expression is not.
    for (const reference of ["${{ secrets['CLAUDE_CODE_OAUTH_TOKEN'] }}", '${{ secrets [ "TOKEN" ] }}', '${{ toJSON(secrets) }}']) {
      const body = `        env:\n          TOKEN: ${reference}\n`;
      assert.match(runnerPlacementViolations(credentialed('', body), 'fixture')[0], rejected, reference);
    }
    assert.deepEqual(runnerPlacementViolations(credentialed('', '        # Uses no secrets.\n'), 'fixture'), []);
    for (const body of ['    permissions:\n      id-token: write\n', '    secrets:\n      TOKEN: ${{ secrets.TOKEN }}\n']) {
      const text = workflow(`  agent:\n    permissions:\n      contents: read\n${body.startsWith('    permissions') ? '      id-token: write\n' : ''}    runs-on: ${SELF_HOSTED_RUNS_ON}\n${body.startsWith('    secrets') ? body : ''}    steps:\n      - run: echo ok\n`);
      assert.match(runnerPlacementViolations(text, 'fixture')[0], rejected, body);
    }
    for (const condition of [
      `    if: ${ISOLATION_CONDITION}\n`,
      `    if: (github.event_name == 'push' || github.ref == 'refs/heads/main') && ${ISOLATION_CONDITION}\n`,
      `    if: \${{ !cancelled() && ${ISOLATION_CONDITION} }}\n`,
      `    if: github.ref == 'refs/heads/main' && ${ISOLATION_CONDITION} && vars.OTHER == 'true'\n`,
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
      `  publish:\n${condition}    runs-on: ${SELF_HOSTED_RUNS_ON}\n${permissions}    steps:\n      - run: gh api --method POST repos/o/r/statuses/abc\n        env:\n          GH_TOKEN: \${{ github.token }}\n`;
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
  });

  it('rejects each repository writer once its isolation condition is removed', () => {
    const writers = [
      ['.github/workflows/agent-architecture.yml', `    if: always() && ${ISOLATION_CONDITION}\n`, '    if: always()\n'],
      ['.github/workflows/agent-review.yml', ` && ${ISOLATION_CONDITION}\n    needs:\n      - context\n      - review\n`, '\n    needs:\n      - context\n      - review\n'],
      ['.github/workflows/agent-head-update.yml', `      ${ISOLATION_CONDITION} &&\n`, ''],
      ['.github/workflows/agent-review-request.yml', `      ${ISOLATION_CONDITION} &&\n`, ''],
      ['.github/workflows/agent-branch-cleanup.yml', `'agent/issue-') && ${ISOLATION_CONDITION}\n`, "'agent/issue-')\n"],
      ['.github/workflows/agent-implement.yml', `dispatch_architecture == 'true' && ${ISOLATION_CONDITION}\n`, "dispatch_architecture == 'true'\n"],
      ['.github/workflows/agent-repair.yml', `dispatch_validation == 'true' && ${ISOLATION_CONDITION}\n`, "dispatch_validation == 'true'\n"],
      ['.github/workflows/validate.yml', `publish_status == 'true' && ${ISOLATION_CONDITION}\n    needs: context\n`, "publish_status == 'true'\n    needs: context\n"],
      ['.github/workflows/validate.yml', `publish_status == 'true' && ${ISOLATION_CONDITION}\n    needs:\n`, "publish_status == 'true'\n    needs:\n"],
      ['.github/workflows/validate.yml', `needs.report-status.result == 'success' && ${ISOLATION_CONDITION}\n`, "needs.report-status.result == 'success'\n"],
      ['.github/workflows/deploy-production.yml', `    if: ${ISOLATION_CONDITION}\n`, ''],
    ];
    for (const [path, gated, ungated] of writers) {
      const text = readFileSync(resolve(path), 'utf8');
      assert.equal(text.split(gated).length, 2, `${path}: fixture text must occur once: ${gated}`);
      const violations = runnerPlacementViolations(text.replace(gated, ungated), path);
      assert.equal(violations.length, 1, `${path}: ${violations.join('; ')}`);
      assert.match(violations[0], /write-capable GITHUB_TOKEN/, path);
    }
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
