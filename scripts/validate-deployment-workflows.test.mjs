import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { readRepositoryFile } from './validate-agent-workflows.mjs';
import {
  deployProductionPath,
  deployProductionRecoveryPath,
  deployProductionReleasePath,
  deployTestingPath,
  extractJobPermissions,
  extractJobs,
  extractStep,
  extractTriggers,
  readWorkflows,
  TESTING_ORDER_STEP,
  verifyDeployProductionRecoveryWorkflow,
  verifyDeployProductionReleaseWorkflow,
  verifyDeployProductionWorkflow,
  verifyDeploymentWorkflows,
  verifyDeployTestingWorkflow,
  verifyNoOtherWorkflowDeploys,
} from './validate-deployment-workflows.mjs';
import { RUNS_ON_SELECTOR } from './validate-runner-placement.mjs';

// Most of the contract is about the shared release workflow, which holds every deployment step.
const deploy = readRepositoryFile(deployProductionReleasePath);
const normal = readRepositoryFile(deployProductionPath);
const recovery = readRepositoryFile(deployProductionRecoveryPath);

function mutate(original, replacement) {
  assert.ok(deploy.includes(original), `fixture text not found: ${original}`);
  return deploy.replace(original, () => replacement);
}

function replaceInJob(jobId, original, replacement) {
  const jobs = extractJobs(deploy, deployProductionReleasePath);
  const jobText = jobs[jobId].trimEnd();
  assert.ok(deploy.includes(jobText), `job text not found: ${jobId}`);
  assert.ok(jobText.includes(original), `fixture text not found in ${jobId}: ${original}`);
  return deploy.replace(jobText, () => jobText.replace(original, () => replacement));
}

test('the repository workflows satisfy the deployment contract', () => {
  verifyDeploymentWorkflows();
});

test('both entry points are triggered only by workflow_dispatch, and the release only by workflow_call', () => {
  assert.deepEqual(extractTriggers(normal), ['workflow_dispatch']);
  assert.deepEqual(extractTriggers(recovery), ['workflow_dispatch']);
  assert.deepEqual(extractTriggers(deploy), ['workflow_call']);
});

test('pushes to main cannot reach Azure: a push trigger on any production workflow is rejected', () => {
  const push = '  push:\n    branches:\n      - main\n';
  assert.throws(() => verifyDeployProductionWorkflow(normal.replace('on:\n', `on:\n${push}`)), /only by workflow_dispatch/);
  assert.throws(() => verifyDeployProductionRecoveryWorkflow(recovery.replace('on:\n', `on:\n${push}`)), /only by workflow_dispatch/);
  const withPush = mutate('on:\n  workflow_call:\n', `on:\n${push}  workflow_call:\n`);
  assert.throws(() => verifyDeployProductionReleaseWorkflow(withPush), /only by workflow_call/);
});

test('pushes to main cannot reach Azure: no other workflow may hold a deployment step', () => {
  const workflows = readWorkflows();
  const vmManager = '.github/workflows/vm-manager.yml';
  assert.ok(vmManager in workflows);
  verifyNoOtherWorkflowDeploys(workflows);

  for (const injected of [
    '      - uses: azure/webapps-deploy@v2\n',
    '      - uses: azure/login@v2\n',
    '      - uses: Azure/static-web-apps-deploy@v1\n',
    '    environment: VmInventoryApi_Env\n',
  ]) {
    const tampered = { ...workflows, [vmManager]: `${workflows[vmManager]}${injected}` };
    assert.throws(() => verifyNoOtherWorkflowDeploys(tampered), /contains forbidden text/);
  }
});

test('manual deployment rejects refs other than main before anything else runs', () => {
  for (const [text, verify] of [[normal, verifyDeployProductionWorkflow], [recovery, verifyDeployProductionRecoveryWorkflow]]) {
    assert.throws(() => verify(text.replace("if: github.ref != 'refs/heads/main'", "if: github.ref == 'refs/heads/never'")), /job 'gate'/);
  }

  const withoutGate = mutate("if: github.ref != 'refs/heads/main'", "if: github.ref == 'refs/heads/never'");
  assert.throws(() => verifyDeployProductionReleaseWorkflow(withoutGate), /refs\/heads\/main/);

  const ungatedApi = replaceInJob('deploy-api', "if: github.ref == 'refs/heads/main'", 'if: true');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(ungatedApi), /deploy-api/);

  const ungatedFrontend = replaceInJob('deploy-frontend', "if: github.ref == 'refs/heads/main'", 'if: true');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(ungatedFrontend), /deploy-frontend/);
});

test('manual deployment rejects SHAs that are not contained in main', () => {
  const withoutAncestry = mutate('merge-base --is-ancestor "$sha" refs/remotes/origin/main', 'true');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(withoutAncestry), /is-ancestor/);

  const withoutRecheck = replaceInJob('deploy-api', '/compare/$RELEASE_SHA...main', '/commits/$RELEASE_SHA');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(withoutRecheck), /compare/);
});

test('backend and frontend deploy artifacts built from the same captured SHA', () => {
  const otherApiArtifact = replaceInJob('deploy-api', 'name: api-${{ needs.resolve.outputs.sha }}', 'name: api-${{ github.sha }}');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(otherApiArtifact), /api-/);

  const otherFrontendArtifact = replaceInJob(
    'deploy-frontend',
    'name: frontend-${{ needs.resolve.outputs.sha }}',
    'name: frontend-${{ github.sha }}',
  );
  assert.throws(() => verifyDeployProductionReleaseWorkflow(otherFrontendArtifact), /frontend-/);

  const rebuildingFrontend = replaceInJob('deploy-frontend', 'skip_app_build: true', 'skip_app_build: false');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(rebuildingFrontend), /skip_app_build/);

  const movingCheckout = replaceInJob('build', 'ref: ${{ needs.resolve.outputs.sha }}', 'ref: main');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(movingCheckout), /job 'build'/);
});

test('failed validation, preflight or backend health prevents downstream deployment', () => {
  for (const dependency of ['validate', 'preflight', 'build']) {
    const ungated = replaceInJob('deploy-api', `      - ${dependency}\n`, '');
    assert.throws(() => verifyDeployProductionReleaseWorkflow(ungated), new RegExp(`must need '${dependency}'`));
  }

  const frontendWithoutHealth = replaceInJob('deploy-frontend', '      - verify-api\n', '');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(frontendWithoutHealth), /must need 'verify-api'/);

  const healthAlways = replaceInJob('verify-api', `    runs-on: ${RUNS_ON_SELECTOR}\n`, `    if: always()\n    runs-on: ${RUNS_ON_SELECTOR}\n`);
  assert.throws(() => verifyDeployProductionReleaseWorkflow(healthAlways), /verify-api/);

  const noValidation = replaceInJob('validate', 'bash scripts/validate.sh', 'echo skipped');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(noValidation), /validate\.sh/);
});

test('deploy jobs keep their environment and permission boundaries', () => {
  const jobs = extractJobs(deploy, deployProductionReleasePath);
  assert.deepEqual(extractJobPermissions(jobs['deploy-api']), { contents: 'read', 'id-token': 'write' });
  assert.deepEqual(extractJobPermissions(jobs['deploy-frontend']), { contents: 'read' });
  assert.deepEqual(extractJobPermissions(jobs.validate), { contents: 'read' });
  assert.deepEqual(extractJobPermissions(jobs['verify-api']), {});

  const widened = replaceInJob('deploy-api', '      id-token: write\n', '      id-token: write\n      actions: write\n');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(widened), /forbidden text: actions: write|permissions must be exactly/);

  const noEnvironment = replaceInJob('deploy-frontend', '    environment: VmInventoryApi_Env\n', '');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(noEnvironment), /environment: VmInventoryApi_Env/);

  const credentialedValidation = replaceInJob('validate', '      contents: read\n', '      contents: read\n      id-token: write\n');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(credentialedValidation), /validate/);

  const secretInValidation = replaceInJob(
    'validate',
    'run: bash scripts/validate.sh',
    'run: bash scripts/validate.sh\n        env:\n          TOKEN: ${{ secrets.AZURE_STATIC_WEB_APPS_API_TOKEN_RED_ISLAND_0C128C000 }}',
  );
  assert.throws(() => verifyDeployProductionReleaseWorkflow(secretInValidation), /validate/);
});

test('a failed deployment cannot push, relabel or start an agent', () => {
  for (const command of ['git push origin main', 'gh workflow run agent-repair.yml', 'gh pr merge 1', 'gh issue edit 1 --add-label agent-ready-claude']) {
    const tampered = replaceInJob('summary', 'echo "## Deploy Production"', `${command}\n            echo "## Deploy Production"`);
    assert.throws(() => verifyDeployProductionReleaseWorkflow(tampered), /forbidden text/);
  }
});

test('the production baseline is recorded only after the API is healthy', () => {
  const recordedOnUpload = replaceInJob('record-release', '      - verify-api\n', '');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(recordedOnUpload), /record-release' must need 'verify-api'/);

  const recordedAlways = replaceInJob('record-release', `    runs-on: ${RUNS_ON_SELECTOR}\n`, `    if: always()\n    runs-on: ${RUNS_ON_SELECTOR}\n`);
  assert.throws(() => verifyDeployProductionReleaseWorkflow(recordedAlways), /record-release/);

  const frontendBeforeRecord = replaceInJob('deploy-frontend', '      - record-release\n', '');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(frontendBeforeRecord), /must need 'record-release'/);
});

test('the API is not deployed until the tool baseline is verified for whichever runner uploads the frontend', () => {
  // Issue #645 review: deploy-frontend's host is chosen only after the API changed, so its Docker
  // prerequisite has to be established for every runner before deploy-api can run.
  const baseline = " && (vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'true' || vars.INVENTORY_RUNNER_BASELINE_VERIFIED == 'true')";
  const apiWithoutBaseline = replaceInJob('deploy-api', baseline, '');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(apiWithoutBaseline), /deploy-api/);
  const frontendWithoutBaseline = replaceInJob('deploy-frontend', baseline, '');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(frontendWithoutBaseline), /deploy-frontend/);
  const silentRefusal = replaceInJob('resolve', '- name: Require a verified tool baseline on every runner', '- name: Something else');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(silentRefusal), /resolve/);
  const gatedAlways = replaceInJob('record-release', "    if: (vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'true' || vars.INVENTORY_RUNNER_ISOLATION_VERIFIED == 'true')\n", "    if: always() && (vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'true' || vars.INVENTORY_RUNNER_ISOLATION_VERIFIED == 'true')\n");
  assert.throws(() => verifyDeployProductionReleaseWorkflow(gatedAlways), /record-release/);
});

test('GitHub-hosted mode checks the deploy tools before anything changes and still requires approval', () => {
  // Issue #807: the VM baseline variable does not describe a GitHub-hosted runner, so the first job
  // checks the deploy jobs' tools on its own fresh runner instead.
  for (const [original, replacement] of [
    ['if [ "$RUNNER_MODE" != "true" ]; then', 'if false; then'],
    ['for tool in az jq curl docker; do', 'for tool in jq curl; do'],
    ['docker info >/dev/null 2>&1 ||', 'true ||'],
  ]) {
    assert.throws(() => verifyDeployProductionReleaseWorkflow(replaceInJob('resolve', original, replacement)), /resolve/, original);
  }
  const withoutEnvironment = replaceInJob('deploy-api', '    environment: VmInventoryApi_Env\n', '');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(withoutEnvironment), /deploy-api/);
});

test('the normal release exposes no inputs and passes none to the release workflow', () => {
  assert.doesNotMatch(normal.split('\njobs:\n')[0], /inputs:/);
  const withInput = normal.replace('  workflow_dispatch:\n', "  workflow_dispatch:\n    inputs:\n      sha:\n        required: false\n        type: string\n");
  assert.throws(() => verifyDeployProductionWorkflow(withInput), /no inputs/);
  const passingSha = normal.replace('      entry_point: normal\n', '      entry_point: normal\n      sha: ${{ github.sha }}\n');
  assert.throws(() => verifyDeployProductionWorkflow(passingSha), /sha:/);
  const wrongEntry = normal.replace('      entry_point: normal\n', '      entry_point: recovery\n');
  assert.throws(() => verifyDeployProductionWorkflow(wrongEntry), /entry_point: normal/);
});

test('the recovery entry point requires an exact full SHA and passes both inputs through', () => {
  const optionalSha = recovery.replace('        description: Full 40-character main commit SHA to deploy.\n        required: true\n', '        description: Full 40-character main commit SHA to deploy.\n        required: false\n');
  assert.throws(() => verifyDeployProductionRecoveryWorkflow(optionalSha), /inputs/);
  const droppedBaseline = recovery.replace('      baseline_sha: ${{ inputs.baseline_sha }}\n', '');
  assert.throws(() => verifyDeployProductionRecoveryWorkflow(droppedBaseline), /baseline_sha/);
  const wrongEntry = recovery.replace('      entry_point: recovery\n', '      entry_point: normal\n');
  assert.throws(() => verifyDeployProductionRecoveryWorkflow(wrongEntry), /entry_point: recovery/);
});

test('both entry points share one non-cancelling concurrency group and run the same release', () => {
  for (const [text, verify] of [[normal, verifyDeployProductionWorkflow], [recovery, verifyDeployProductionRecoveryWorkflow]]) {
    assert.throws(() => verify(text.replace('  group: deploy-production\n', '  group: deploy-production-other\n')), /concurrency/);
    assert.throws(() => verify(text.replace('  cancel-in-progress: false\n', '  cancel-in-progress: true\n')), /concurrency|cancel-in-progress: true/);
    assert.throws(() => verify(text.replace('uses: ./.github/workflows/deploy-production-release.yml', 'uses: ./.github/workflows/other-release.yml')), /deploy-production-release/);
    assert.throws(() => verify(text.replace('    needs: gate\n', '')), /must need 'gate'/);
    assert.throws(() => verify(text.replace('    secrets: inherit\n', '    secrets:\n      TOKEN: ${{ secrets.AZURE_STATIC_WEB_APPS_API_TOKEN_RED_ISLAND_0C128C000 }}\n')), /secrets: inherit|forbidden/);
  }
  const ownGroup = deploy.replace('permissions: {}\n', 'permissions: {}\n\nconcurrency:\n  group: deploy-production\n  cancel-in-progress: false\n');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(ownGroup), /concurrency/);
});

test('no other workflow may call the release workflow', () => {
  const workflows = readWorkflows();
  const vmManager = '.github/workflows/vm-manager.yml';
  const tampered = { ...workflows, [vmManager]: `${workflows[vmManager]}  sneaky:\n    uses: ./.github/workflows/deploy-production-release.yml\n` };
  assert.throws(() => verifyNoOtherWorkflowDeploys(tampered), /deploy-production-release/);
  const { [deployProductionRecoveryPath]: _removed, ...withoutRecovery } = workflows;
  assert.throws(() => verifyDeploymentWorkflows(withoutRecovery), /missing/);
});

test('the release never falls back to the release SHA as the migration baseline', () => {
  const guessed = replaceInJob('resolve', 'baseline="$recorded"', 'baseline="$sha"');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(guessed), /baseline="\$sha"/);
  const dispatchFromHead = replaceInJob('resolve', 'sha="$GITHUB_SHA"', 'sha="$(git rev-parse refs/remotes/origin/main)"');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(dispatchFromHead), /GITHUB_SHA/);
  const operatorBaselineAnywhere = replaceInJob('resolve', 'elif [ "$ENTRY_POINT" = "recovery" ] && [ -n "$INPUT_BASELINE" ]; then', 'elif [ -n "$INPUT_BASELINE" ]; then');
  assert.throws(() => verifyDeployProductionReleaseWorkflow(operatorBaselineAnywhere), /recovery/);
});

// Behaviour of the two resolve steps, run in bash against a throwaway repository and a fake gh.
// The extracted text is exactly what GitHub runs, so these cover the release and baseline rules.
function stepScript(jobText, name) {
  const lines = jobText.split('\n');
  const start = lines.indexOf(`      - name: ${name}`);
  assert.ok(start >= 0, `missing step: ${name}`);
  const runAt = lines.indexOf('        run: |', start);
  assert.ok(runAt > start, `step has no run block: ${name}`);
  const body = [];
  for (const line of lines.slice(runAt + 1)) {
    if (line.trim() !== '' && !line.startsWith(' '.repeat(10))) {
      break;
    }
    body.push(line.slice(10));
  }
  return body.join('\n');
}

const resolveJob = extractJobs(deploy, deployProductionReleasePath).resolve;
const releaseScript = stepScript(resolveJob, 'Resolve and verify the release SHA');
const baselineScript = stepScript(resolveJob, 'Resolve the production baseline');

function git(cwd, ...args) {
  const result = spawnSync('git', args, { cwd, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  return result.stdout.trim();
}

// History: baseline -> release (dispatched) -> later (main advanced after dispatch). `outside` is
// a commit that is not in main.
function makeRepository() {
  const root = mkdtempSync(join(tmpdir(), 'deploy-resolve-'));
  const repo = join(root, 'repo');
  git(root, 'init', '--quiet', repo);
  git(repo, 'config', 'user.email', 'test@example.invalid');
  git(repo, 'config', 'user.name', 'Test');
  const commit = (message) => {
    git(repo, 'commit', '--quiet', '--allow-empty', '-m', message);
    return git(repo, 'rev-parse', 'HEAD');
  };
  const baseline = commit('baseline');
  const release = commit('release');
  const later = commit('later');
  git(repo, 'update-ref', 'refs/remotes/origin/main', later);
  git(repo, 'checkout', '--quiet', '--orphan', 'elsewhere');
  const outside = commit('outside');
  const bin = join(root, 'bin');
  mkdirSync(bin);
  writeFileSync(join(bin, 'gh'), '#!/usr/bin/env bash\nprintf "%s\\n" "$*" >> "$FAKE_GH_LOG"\n[ "${FAKE_GH_EXIT:-0}" = 0 ] || exit "$FAKE_GH_EXIT"\nprintf "%s" "$FAKE_GH_RECORDED"\n');
  chmodSync(join(bin, 'gh'), 0o755);
  return { root, repo, bin, baseline, release, later, outside };
}

const fixture = makeRepository();
test.after(() => rmSync(fixture.root, { recursive: true, force: true }));

function runStep(script, { entry, sha = '', baseline = '', recorded = '', ghExit = 0 }) {
  const output = join(fixture.root, `output-${Math.random().toString(36).slice(2)}`);
  writeFileSync(output, '');
  const result = spawnSync('bash', ['-c', script], {
    cwd: fixture.repo,
    encoding: 'utf8',
    env: {
      PATH: `${fixture.bin}:${process.env.PATH}`,
      HOME: fixture.root,
      GITHUB_SHA: fixture.release,
      GITHUB_OUTPUT: output,
      GITHUB_REPOSITORY: 'owner/repo',
      PRODUCTION_ENVIRONMENT: 'VmInventoryApi_Env',
      RELEASE_RECORD_TASK: 'deploy:production-backend',
      GH_TOKEN: 'fake',
      ENTRY_POINT: entry,
      INPUT_SHA: sha,
      INPUT_BASELINE: baseline,
      FAKE_GH_RECORDED: recorded,
      FAKE_GH_EXIT: String(ghExit),
      FAKE_GH_LOG: join(fixture.root, 'gh.log'),
    },
  });
  const outputs = Object.fromEntries(
    readFileSync(output, 'utf8').split('\n').filter(Boolean).map((line) => [line.slice(0, line.indexOf('=')), line.slice(line.indexOf('=') + 1)]),
  );
  return { status: result.status, log: `${result.stdout}${result.stderr}`, outputs };
}

test('a normal release deploys the dispatch commit even after main has advanced', () => {
  const run = runStep(releaseScript, { entry: 'normal' });
  assert.equal(run.status, 0, run.log);
  assert.equal(run.outputs.sha, fixture.release);
  assert.notEqual(run.outputs.sha, fixture.later);
});

test('a normal release refuses a supplied commit or baseline, and unknown callers are refused', () => {
  for (const inputs of [{ sha: fixture.later }, { baseline: fixture.baseline }]) {
    const run = runStep(releaseScript, { entry: 'normal', ...inputs });
    assert.equal(run.status, 1, run.log);
    assert.match(run.log, /takes no sha or baseline_sha/);
    assert.deepEqual(run.outputs, {});
  }
  const unknown = runStep(releaseScript, { entry: 'other' });
  assert.equal(unknown.status, 1);
  assert.match(unknown.log, /Unknown entry point/);
});

test('a recovery release deploys exactly the supplied main commit and nothing outside main', () => {
  const run = runStep(releaseScript, { entry: 'recovery', sha: fixture.baseline });
  assert.equal(run.status, 0, run.log);
  assert.equal(run.outputs.sha, fixture.baseline);

  for (const [sha, message] of [['', /requires sha/], [fixture.release.slice(0, 12), /full 40-character/], [fixture.outside, /not contained in main/]]) {
    const refused = runStep(releaseScript, { entry: 'recovery', sha });
    assert.equal(refused.status, 1, refused.log);
    assert.match(refused.log, message);
    assert.deepEqual(refused.outputs, {});
  }
});

test('the baseline is resolved automatically from the newest release record', () => {
  for (const entry of ['normal', 'recovery']) {
    const run = runStep(baselineScript, { entry, recorded: fixture.baseline });
    assert.equal(run.status, 0, run.log);
    assert.deepEqual(run.outputs, { baseline_sha: fixture.baseline, baseline_source: 'recorded' });
  }
  const log = readFileSync(join(fixture.root, 'gh.log'), 'utf8');
  assert.match(log, /environment=VmInventoryApi_Env&task=deploy:production-backend&per_page=1/);
});

test('a missing record stops the normal release and points to the recovery bootstrap, never guessing', () => {
  const run = runStep(baselineScript, { entry: 'normal' });
  assert.equal(run.status, 1, run.log);
  assert.match(run.log, /Deploy Production \(advanced recovery\)/);
  assert.match(run.log, new RegExp(`set sha to ${fixture.release}`));
  assert.deepEqual(run.outputs, {});
});

test('recovery bootstraps an operator baseline only when no release is recorded', () => {
  const bootstrap = runStep(baselineScript, { entry: 'recovery', baseline: fixture.baseline });
  assert.equal(bootstrap.status, 0, bootstrap.log);
  assert.deepEqual(bootstrap.outputs, { baseline_sha: fixture.baseline, baseline_source: 'operator input (no release recorded)' });

  const none = runStep(baselineScript, { entry: 'recovery' });
  assert.equal(none.status, 1);
  assert.match(none.log, /baseline_sha set to the main commit production runs now/);

  const conflicting = runStep(baselineScript, { entry: 'recovery', baseline: fixture.release, recorded: fixture.baseline });
  assert.equal(conflicting.status, 1, conflicting.log);
  assert.match(conflicting.log, /differs from the last recorded production release/);
  assert.deepEqual(conflicting.outputs, {});

  const matching = runStep(baselineScript, { entry: 'recovery', baseline: fixture.baseline, recorded: fixture.baseline });
  assert.equal(matching.status, 0, matching.log);
  assert.equal(matching.outputs.baseline_source, 'recorded');

  for (const baseline of [fixture.outside, 'not-a-sha']) {
    const refused = runStep(baselineScript, { entry: 'recovery', baseline });
    assert.equal(refused.status, 1, refused.log);
    assert.deepEqual(refused.outputs, {});
  }
});

test('an invalid or unreadable release record stops the run before anything is deployed', () => {
  for (const recorded of [fixture.outside, 'abc123', '0'.repeat(40)]) {
    for (const entry of ['normal', 'recovery']) {
      const run = runStep(baselineScript, { entry, recorded, baseline: entry === 'recovery' ? fixture.baseline : '' });
      assert.equal(run.status, 1, run.log);
      assert.match(run.log, /delete that deployment record/);
      assert.match(run.log, /an inactive record is still listed and would be selected again/);
      assert.match(run.log, /only when no record is left/);
      assert.deepEqual(run.outputs, {});
    }
  }
  const unreadable = runStep(baselineScript, { entry: 'normal', ghExit: 1 });
  assert.equal(unreadable.status, 1);
  assert.match(unreadable.log, /Could not read the recorded production releases.*Re-run/);
  assert.deepEqual(unreadable.outputs, {});
});

test('an inactive invalid record is still selected, and an older record wins over an operator baseline', () => {
  // The lookup lists deployments by environment and task, not by status: whatever the API returns
  // first is the baseline, so marking a bad record inactive does not reach the bootstrap branch.
  assert.match(baselineScript, /deployments\?environment=\$PRODUCTION_ENVIRONMENT&task=\$RELEASE_RECORD_TASK&per_page=1/);
  assert.doesNotMatch(baselineScript, /statuses/);
  const stillInvalid = runStep(baselineScript, { entry: 'recovery', recorded: fixture.outside, baseline: fixture.baseline });
  assert.equal(stillInvalid.status, 1, stillInvalid.log);
  assert.deepEqual(stillInvalid.outputs, {});
  // After the bad record is deleted, the next newest record is used and a different operator
  // baseline is still refused.
  const olderRecord = runStep(baselineScript, { entry: 'recovery', recorded: fixture.baseline, baseline: fixture.release });
  assert.equal(olderRecord.status, 1, olderRecord.log);
  assert.match(olderRecord.log, /differs from the last recorded production release/);
});

// ---------------------------------------------------------------------------------------
// Deploy Testing (deploy-testing.yml): the hosted testing environment, deployed from develop.
// ---------------------------------------------------------------------------------------

const testing = readRepositoryFile(deployTestingPath);
const testingJobs = extractJobs(testing, deployTestingPath);

function mutateTesting(original, replacement) {
  assert.ok(testing.includes(original), `fixture text not found: ${original}`);
  return testing.replace(original, () => replacement);
}

function replaceInTestingJob(jobId, original, replacement) {
  const jobText = testingJobs[jobId].trimEnd();
  assert.ok(testing.includes(jobText), `job text not found: ${jobId}`);
  assert.ok(jobText.includes(original), `fixture text not found in ${jobId}: ${original}`);
  return testing.replace(jobText, () => jobText.replace(original, () => replacement));
}

test('testing deploys on every push to develop and on a manual dispatch with no inputs, nothing else', () => {
  verifyDeployTestingWorkflow(testing);
  assert.deepEqual(extractTriggers(testing), ['push', 'workflow_dispatch']);
  for (const tampered of [
    mutateTesting('      - develop\n', '      - develop\n      - main\n'),
    mutateTesting('      - develop\n', "      - '**'\n"),
    mutateTesting('  push:\n    branches:\n', '  push:\n    tags:\n      - v*\n    branches:\n'),
    mutateTesting('  workflow_dispatch:\n', '  workflow_dispatch:\n    inputs:\n      sha:\n        type: string\n'),
    mutateTesting('  workflow_dispatch:\n', '  workflow_dispatch:\n  pull_request:\n'),
    mutateTesting('  workflow_dispatch:\n', '  workflow_dispatch:\n  schedule:\n    - cron: "0 0 * * *"\n'),
  ]) {
    assert.throws(() => verifyDeployTestingWorkflow(tampered), /on: block|triggered only/);
  }
});

test('a manual testing run started from any branch other than develop is refused before anything runs', () => {
  const ungatedResolve = mutateTesting("if: github.ref != 'refs/heads/develop'", "if: github.ref == 'refs/heads/never'");
  assert.throws(() => verifyDeployTestingWorkflow(ungatedResolve), /refs\/heads\/develop/);
  for (const jobId of ['deploy-api', 'deploy-frontend']) {
    const ungated = replaceInTestingJob(jobId, "if: github.ref == 'refs/heads/develop' && ", 'if: ');
    assert.throws(() => verifyDeployTestingWorkflow(ungated), new RegExp(jobId));
  }
});

test('testing builds and deploys exactly the triggering commit and never reads a newer develop head', () => {
  for (const jobId of ['validate', 'build']) {
    const movingCheckout = replaceInTestingJob(jobId, 'ref: ${{ needs.resolve.outputs.sha }}', 'ref: develop');
    assert.throws(() => verifyDeployTestingWorkflow(movingCheckout), /every checkout must use ref/);
  }
  const fetchedHead = replaceInTestingJob('build', '      - name: Publish API\n', '      - name: Fetch develop\n        run: git fetch origin develop\n\n      - name: Publish API\n');
  assert.throws(() => verifyDeployTestingWorkflow(fetchedHead), /git fetch/);
  const otherApiArtifact = replaceInTestingJob('deploy-api', 'name: testing-api-${{ needs.resolve.outputs.sha }}', 'name: testing-api-${{ github.run_id }}');
  assert.throws(() => verifyDeployTestingWorkflow(otherApiArtifact), /testing-api-/);
  const otherFrontendArtifact = replaceInTestingJob('deploy-frontend', 'name: testing-frontend-${{ needs.resolve.outputs.sha }}', 'name: testing-frontend-latest');
  assert.throws(() => verifyDeployTestingWorkflow(otherFrontendArtifact), /testing-frontend-/);
  const rebuildingFrontend = replaceInTestingJob('deploy-frontend', 'skip_app_build: true', 'skip_app_build: false');
  assert.throws(() => verifyDeployTestingWorkflow(rebuildingFrontend), /skip_app_build/);
  const headSha = replaceInTestingJob('resolve', 'sha="$GITHUB_SHA"', 'sha="$(gh api repos/$GITHUB_REPOSITORY/commits/develop --jq .sha)"');
  assert.throws(() => verifyDeployTestingWorkflow(headSha), /GITHUB_SHA/);
});

test('failed validation, build or test API health prevents the testing deployment that follows', () => {
  for (const dependency of ['validate', 'build']) {
    const ungated = replaceInTestingJob('deploy-api', `      - ${dependency}\n`, '');
    assert.throws(() => verifyDeployTestingWorkflow(ungated), new RegExp(`must need '${dependency}'`));
  }
  for (const dependency of ['verify-api', 'record-deployment']) {
    const ungated = replaceInTestingJob('deploy-frontend', `      - ${dependency}\n`, '');
    assert.throws(() => verifyDeployTestingWorkflow(ungated), new RegExp(`must need '${dependency}'`));
  }
  const noValidation = replaceInTestingJob('validate', 'bash scripts/validate.sh', 'echo skipped');
  assert.throws(() => verifyDeployTestingWorkflow(noValidation), /validate\.sh/);
  const healthAlways = replaceInTestingJob('verify-api', `    runs-on: ${RUNS_ON_SELECTOR}\n`, `    if: always()\n    runs-on: ${RUNS_ON_SELECTOR}\n`);
  assert.throws(() => verifyDeployTestingWorkflow(healthAlways), /verify-api/);
  const noHealth = replaceInTestingJob('verify-api', '"$base/health/ready"', '"$base/health/live"');
  assert.throws(() => verifyDeployTestingWorkflow(noHealth), /health\/ready/);
  const recordAlways = replaceInTestingJob('record-deployment', "    if: (vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'true' || vars.INVENTORY_RUNNER_ISOLATION_VERIFIED == 'true')\n", "    if: always() && (vars.INVENTORY_AGENT_SELF_HOSTED_ALL != 'true' || vars.INVENTORY_RUNNER_ISOLATION_VERIFIED == 'true')\n");
  assert.throws(() => verifyDeployTestingWorkflow(recordAlways), /record-deployment/);
  const frontendUnverified = replaceInTestingJob('deploy-frontend', '- name: Verify the test frontend responds and calls the test API', '- name: Done');
  assert.throws(() => verifyDeployTestingWorkflow(frontendUnverified), /Verify the test frontend/);
  const configUnchecked = replaceInTestingJob('deploy-frontend', ' && [ "$configured" = "$expected" ]', '');
  assert.throws(() => verifyDeployTestingWorkflow(configUnchecked), /configured/);
});

test('testing uses only its own environment and OIDC identity, never a production credential or a stored secret', () => {
  for (const [original, replacement] of [
    ['client-id: ${{ vars.TESTING_AZURE_CLIENT_ID }}', 'client-id: ${{ vars.VmInventoryApi_CLIENT_ID_4D7F }}'],
    ['azure_static_web_apps_api_token: ${{ steps.swa-token.outputs.token }}', 'azure_static_web_apps_api_token: ${{ secrets.AZURE_STATIC_WEB_APPS_API_TOKEN_RED_ISLAND_0C128C000 }}'],
    ['azure_static_web_apps_api_token: ${{ steps.swa-token.outputs.token }}', 'azure_static_web_apps_api_token: ${{ secrets.TESTING_SWA_TOKEN }}'],
    ['    environment: testing\n', '    environment: VmInventoryApi_Env\n'],
    ['    environment: testing\n', '    environment: price-collector\n'],
    ['app-name: ${{ vars.TESTING_API_APP_NAME }}', 'app-name: vm-manager'],
    ['app-name: ${{ vars.TESTING_API_APP_NAME }}', 'app-name: ${{ vars.TESTING_API_APP_NAME }}\n          publish-profile: ${{ vars.PROFILE }}'],
  ]) {
    assert.throws(() => verifyDeployTestingWorkflow(mutateTesting(original, replacement)), /forbidden|missing required|environment/);
  }
  const jobs = testingJobs;
  assert.deepEqual(extractJobPermissions(jobs['deploy-api']), { contents: 'read', deployments: 'read', 'id-token': 'write' });
  assert.deepEqual(extractJobPermissions(jobs['deploy-frontend']), { contents: 'read', 'id-token': 'write' });
  assert.deepEqual(extractJobPermissions(jobs.validate), { contents: 'read' });
  const writer = replaceInTestingJob('deploy-api', '      deployments: read\n', '      deployments: read\n      contents: write\n');
  assert.throws(() => verifyDeployTestingWorkflow(writer), /contents: write|permissions/);
  // An OIDC token or the testing environment in a job that does not deploy is refused.
  const oidcInValidate = replaceInTestingJob('validate', '      contents: read\n', '      contents: read\n      id-token: write\n');
  assert.throws(() => verifyDeployTestingWorkflow(oidcInValidate), /validate/);
});

test('production and every other workflow are refused the testing environment and identity, and production keeps its own', () => {
  const workflows = readWorkflows();
  verifyNoOtherWorkflowDeploys(workflows);
  for (const path of [deployProductionReleasePath, deployProductionPath, '.github/workflows/vm-manager.yml', '.github/workflows/price-collector.yml']) {
    for (const injected of ['    environment: testing\n', '          client-id: ${{ vars.TESTING_AZURE_CLIENT_ID }}\n']) {
      const tampered = { ...workflows, [path]: `${workflows[path]}${injected}` };
      assert.throws(() => verifyNoOtherWorkflowDeploys(tampered), /forbidden text/);
    }
  }
  // Allowing testing did not open a second production path: other workflows still may not deploy.
  const vmManager = '.github/workflows/vm-manager.yml';
  const deploying = { ...workflows, [vmManager]: `${workflows[vmManager]}      - uses: azure/webapps-deploy@v2\n` };
  assert.throws(() => verifyNoOtherWorkflowDeploys(deploying), /forbidden text/);
  // And production still runs only from main, with its own environment and identity.
  verifyDeployProductionReleaseWorkflow(deploy);
  assert.ok(deploy.includes('environment: VmInventoryApi_Env'));
  assert.ok(!testing.includes('VmInventoryApi'));
});

test('testing runs share one non-cancelling queue and the order rule is checked again before Azure', () => {
  for (const tampered of [
    mutateTesting('  cancel-in-progress: false\n', '  cancel-in-progress: true\n'),
    mutateTesting('  group: deploy-testing\n', '  group: deploy-testing-${{ github.sha }}\n'),
    mutateTesting('concurrency:\n  group: deploy-testing\n  cancel-in-progress: false\n', ''),
    replaceInTestingJob('deploy-api', '    environment: testing\n', '    environment: testing\n    concurrency:\n      group: other\n'),
  ]) {
    assert.throws(() => verifyDeployTestingWorkflow(tampered), /concurrency|cancel-in-progress/);
  }
  const noRecheck = replaceInTestingJob('deploy-api', `- name: ${TESTING_ORDER_STEP}`, '- name: Skip the order check');
  assert.throws(() => verifyDeployTestingWorkflow(noRecheck), /order|missing step|must come before/);
  const weakerRecheck = replaceInTestingJob('deploy-api', 'behind) fail', 'behind) echo');
  assert.throws(() => verifyDeployTestingWorkflow(weakerRecheck), /repeat the resolve job's step exactly/);
  const weakerResolve = replaceInTestingJob('resolve', 'behind) fail', 'behind) echo');
  assert.throws(() => verifyDeployTestingWorkflow(weakerResolve), /behind\) fail/);
});

test('testing keeps the test database and documents outside the package and runs no scheduled job', () => {
  for (const [original, replacement] of [
    ['^Data\\ Source=/home/data/', '^Data\\ Source='],
    ['[ "$(setting DocumentStorage__Provider)" = "AzureBlob" ]', 'true'],
    ['[ "$(setting ASPNETCORE_ENVIRONMENT)" = "Staging" ]', 'true'],
  ]) {
    assert.throws(() => verifyDeployTestingWorkflow(replaceInTestingJob('deploy-api', original, replacement)), /deploy-api/);
  }
  const keepsJobs = replaceInTestingJob('build', 'rm -rf -- "$RUNNER_TEMP/api/App_Data/jobs"', 'true');
  assert.throws(() => verifyDeployTestingWorkflow(keepsJobs), /App_Data\/jobs/);
});

// Behaviour of the testing steps, run in bash exactly as GitHub runs them, with a fake gh/az.
const testingFixtureRoot = mkdtempSync(join(tmpdir(), 'deploy-testing-'));
test.after(() => rmSync(testingFixtureRoot, { recursive: true, force: true }));
const testingBin = join(testingFixtureRoot, 'bin');
mkdirSync(testingBin);
// gh answers by endpoint: the develop comparison, the newest record, and the record comparison.
writeFileSync(join(testingBin, 'gh'), `#!/usr/bin/env bash
case "$*" in
  *"/deployments?"*) [ "\${FAKE_RECORDS_EXIT:-0}" = 0 ] || exit 1; printf "%s" "$FAKE_RECORDED" ;;
  *"...develop"*) [ "\${FAKE_DEVELOP_EXIT:-0}" = 0 ] || exit 1; printf "%s" "$FAKE_CONTAINED" ;;
  *"/compare/"*) printf "%s" "$FAKE_ORDER" ;;
  *) echo "unexpected gh call: $*" >&2; exit 2 ;;
esac
`);
chmodSync(join(testingBin, 'gh'), 0o755);
// az returns the App Service settings fixture; it is never printed by the step.
writeFileSync(join(testingBin, 'az'), `#!/usr/bin/env bash
[ "\${FAKE_AZ_EXIT:-0}" = 0 ] || exit 1
case "$*" in
  "staticwebapp show "*) printf "%s" "$FAKE_SWA_HOST" ;;
  "staticwebapp secrets list "*) printf "%s" "$FAKE_SWA_TOKEN" ;;
  *) printf "%s" "$FAKE_SETTINGS" ;;
esac
`);
chmodSync(join(testingBin, 'az'), 0o755);

const SHA = 'a'.repeat(40);
const RECORDED = 'b'.repeat(40);

function runTestingStep(script, env = {}, cwd = testingFixtureRoot) {
  const output = join(testingFixtureRoot, `output-${Math.random().toString(36).slice(2)}`);
  writeFileSync(output, '');
  const result = spawnSync('bash', ['-c', script], {
    cwd,
    encoding: 'utf8',
    env: {
      PATH: `${testingBin}:${process.env.PATH}`,
      HOME: testingFixtureRoot,
      GITHUB_OUTPUT: output,
      GITHUB_REPOSITORY: 'owner/repo',
      GITHUB_SHA: SHA,
      TESTING_ENVIRONMENT: 'testing',
      TESTING_RECORD_TASK: 'deploy:testing-backend',
      PRODUCTION_API_HOST: 'vm-manager-axhadxh5hjehayh0.australiaeast-01.azurewebsites.net',
      PRODUCTION_FRONTEND_HOST: 'red-island-0c128c000.7.azurestaticapps.net',
      GH_TOKEN: 'fake',
      ...env,
    },
  });
  const outputs = Object.fromEntries(
    readFileSync(output, 'utf8').split('\n').filter(Boolean).map((line) => [line.slice(0, line.indexOf('=')), line.slice(line.indexOf('=') + 1)]),
  );
  return { status: result.status, log: `${result.stdout}${result.stderr}`, outputs };
}

const orderScript = stepScript(testingJobs.resolve, TESTING_ORDER_STEP);

test('the order step is byte-identical in resolve and deploy-api', () => {
  assert.equal(stepScript(testingJobs['deploy-api'], TESTING_ORDER_STEP).trimEnd(), orderScript.trimEnd());
  assert.equal(extractStep(testingJobs['deploy-api'], TESTING_ORDER_STEP, deployTestingPath).trimEnd(), extractStep(testingJobs.resolve, TESTING_ORDER_STEP, deployTestingPath).trimEnd());
});

test('the first testing deployment, and every newer or identical commit, is allowed', () => {
  for (const [recorded, order] of [['', ''], [RECORDED, 'ahead'], [SHA, 'identical']]) {
    const run = runTestingStep(orderScript, { FAKE_CONTAINED: 'ahead', FAKE_RECORDED: recorded, FAKE_ORDER: order });
    assert.equal(run.status, 0, run.log);
    assert.equal(run.outputs.sha, SHA);
  }
  const head = runTestingStep(orderScript, { FAKE_CONTAINED: 'identical', FAKE_RECORDED: '' });
  assert.equal(head.status, 0, head.log);
});

test('an older commit never overwrites a newer testing deployment', () => {
  const run = runTestingStep(orderScript, { FAKE_CONTAINED: 'ahead', FAKE_RECORDED: RECORDED, FAKE_ORDER: 'behind' });
  assert.notEqual(run.status, 0);
  assert.match(run.log, /newer than/);
  assert.equal(run.outputs.sha, undefined);
  const diverged = runTestingStep(orderScript, { FAKE_CONTAINED: 'ahead', FAKE_RECORDED: RECORDED, FAKE_ORDER: 'diverged' });
  assert.notEqual(diverged.status, 0);
  assert.match(diverged.log, /diverged/);
});

test('a commit outside develop, an invalid record or an unreadable API stops the run before deploying', () => {
  for (const [env, message] of [
    [{ FAKE_CONTAINED: 'behind' }, /not contained in develop/],
    [{ FAKE_CONTAINED: 'diverged' }, /not contained in develop/],
    [{ FAKE_DEVELOP_EXIT: '1' }, /Could not compare/],
    [{ FAKE_CONTAINED: 'ahead', FAKE_RECORDS_EXIT: '1' }, /Could not read the recorded testing deployments/],
    [{ FAKE_CONTAINED: 'ahead', FAKE_RECORDED: 'main' }, /not a full commit SHA/],
    [{ GITHUB_SHA: 'develop', FAKE_CONTAINED: 'ahead' }, /full 40-character/],
  ]) {
    const run = runTestingStep(orderScript, env);
    assert.notEqual(run.status, 0, run.log);
    assert.match(run.log, message);
    assert.equal(run.outputs.sha, undefined);
  }
});

const configScript = stepScript(testingJobs['deploy-api'], 'Check the testing environment configuration');
const GUID = '00000000-0000-4000-8000-000000000000';
const validConfig = {
  AZURE_CLIENT_ID: GUID,
  AZURE_TENANT_ID: GUID,
  AZURE_SUBSCRIPTION_ID: GUID,
  RESOURCE_GROUP: 'restockroo-testing',
  API_APP_NAME: 'restockroo-testing-api',
  API_BASE_URL: 'https://restockroo-testing-api.azurewebsites.net/',
};

test('the test API configuration must be complete and must not name production', () => {
  const ok = runTestingStep(configScript, validConfig);
  assert.equal(ok.status, 0, ok.log);
  assert.equal(ok.outputs.api_base_url, 'https://restockroo-testing-api.azurewebsites.net');
  for (const [override, message] of [
    [{ AZURE_CLIENT_ID: '' }, /TESTING_AZURE_CLIENT_ID/],
    [{ API_APP_NAME: 'vm-manager' }, /production App Service/],
    [{ API_BASE_URL: 'https://VM-MANAGER-axhadxh5hjehayh0.australiaeast-01.azurewebsites.net' }, /production API/],
    [{ API_BASE_URL: 'http://restockroo-testing-api.azurewebsites.net' }, /https origin/],
    [{ API_BASE_URL: 'https://restockroo-testing-api.azurewebsites.net/api' }, /https origin/],
  ]) {
    const run = runTestingStep(configScript, { ...validConfig, ...override });
    assert.notEqual(run.status, 0, run.log);
    assert.match(run.log, message);
  }
});

const dataScript = stepScript(testingJobs['deploy-api'], 'Check the test API keeps its data outside the package');
const goodSettings = {
  ASPNETCORE_ENVIRONMENT: 'Staging',
  Database__AllowAutomaticMigrationUnsafeOutsideDevelopment: 'true',
  ConnectionStrings__DefaultConnection: 'Data Source=/home/data/inventory-testing.db',
  DocumentStorage__Provider: 'AzureBlob',
  DocumentStorage__BlobServiceUri: 'https://restockrootesting.blob.core.windows.net',
  DocumentStorage__ContainerName: 'business-documents-testing',
};
const settingsJson = (values) => JSON.stringify(Object.entries(values).map(([name, value]) => ({ name, value, slotSetting: false })));

test('the test API is deployed only while its database and documents live outside the package', () => {
  const ok = runTestingStep(dataScript, { RESOURCE_GROUP: 'rg', API_APP_NAME: 'app', FAKE_SETTINGS: settingsJson(goodSettings) });
  assert.equal(ok.status, 0, ok.log);
  // The connection string and other values are compared, never printed.
  assert.ok(!ok.log.includes('inventory-testing.db'), ok.log);
  for (const [override, message] of [
    [{ ASPNETCORE_ENVIRONMENT: 'Production' }, /must be Staging/],
    [{ ASPNETCORE_ENVIRONMENT: undefined }, /must be Staging/],
    [{ Database__AllowAutomaticMigrationUnsafeOutsideDevelopment: 'false' }, /AllowAutomaticMigration/],
    [{ ConnectionStrings__DefaultConnection: 'Data Source=inventory.db' }, /outside the deployed package/],
    [{ ConnectionStrings__DefaultConnection: 'Data Source=/home/site/wwwroot/inventory.db' }, /outside the deployed package/],
    [{ ConnectionStrings__DefaultConnection: 'Data Source=/home/data/../site/wwwroot/inventory.db' }, /outside the deployed package/],
    [{ DocumentStorage__Provider: 'FileSystem' }, /AzureBlob/],
    [{ DocumentStorage__BlobServiceUri: 'https://evil.example' }, /blob endpoint/],
    [{ DocumentStorage__ContainerName: '' }, /ContainerName/],
  ]) {
    const values = { ...goodSettings, ...override };
    for (const [name, value] of Object.entries(values)) if (value === undefined) delete values[name];
    const run = runTestingStep(dataScript, { RESOURCE_GROUP: 'rg', API_APP_NAME: 'app', FAKE_SETTINGS: settingsJson(values) });
    assert.notEqual(run.status, 0, run.log);
    assert.match(run.log, message);
    assert.match(run.log, /Nothing was deployed/);
  }
  const unreadable = runTestingStep(dataScript, { RESOURCE_GROUP: 'rg', API_APP_NAME: 'app', FAKE_AZ_EXIT: '1' });
  assert.notEqual(unreadable.status, 0);
  assert.match(unreadable.log, /Could not read the test App Service settings/);
});

test('a test API that could make real Nayax calls or scheduled backups is reported', () => {
  const run = runTestingStep(dataScript, {
    RESOURCE_GROUP: 'rg',
    API_APP_NAME: 'app',
    FAKE_SETTINGS: settingsJson({ ...goodSettings, NayaxTokenProtection__ActiveKeyId: 'k1', BackupStorage__BlobServiceUri: 'https://x.blob.core.windows.net' }),
  });
  assert.equal(run.status, 0, run.log);
  assert.match(run.log, /::warning::Nayax token protection is configured/);
  assert.match(run.log, /::warning::BackupStorage is configured/);
});

const pointScript = stepScript(testingJobs['deploy-frontend'], 'Point the frontend at the test API');

test('the test frontend bundle is pointed at the test API and never at the production API', () => {
  const site = join(testingFixtureRoot, 'site');
  mkdirSync(join(site, 'testing-frontend', 'assets'), { recursive: true });
  const config = join(site, 'testing-frontend', 'assets', 'config.json');
  writeFileSync(config, readRepositoryFile('frontend/inventory-app/src/assets/config.json'));
  const ok = runTestingStep(pointScript, { API_BASE_URL: 'https://restockroo-testing-api.azurewebsites.net' }, site);
  assert.equal(ok.status, 0, ok.log);
  assert.deepEqual(JSON.parse(readFileSync(config, 'utf8')), { apiBaseUrl: 'https://restockroo-testing-api.azurewebsites.net/api' });

  const production = runTestingStep(pointScript, { API_BASE_URL: 'https://vm-manager-axhadxh5hjehayh0.australiaeast-01.azurewebsites.net' }, site);
  assert.notEqual(production.status, 0);
  assert.match(production.log, /must not call the production API/);

  rmSync(config);
  const missing = runTestingStep(pointScript, { API_BASE_URL: 'https://restockroo-testing-api.azurewebsites.net' }, site);
  assert.notEqual(missing.status, 0);
  assert.match(missing.log, /no assets\/config.json/);
});

test('the production API host the testing workflow refuses is the one the production frontend calls', () => {
  const productionApi = new URL(JSON.parse(readRepositoryFile('frontend/inventory-app/src/assets/config.json')).apiBaseUrl).host;
  assert.ok(testing.includes(`  PRODUCTION_API_HOST: ${productionApi}\n`));
});

const tokenScript = stepScript(testingJobs['deploy-frontend'], 'Read the test Static Web App deployment token');

test('the test Static Web App token is read with the testing identity, masked first, and never production', () => {
  const env = { RESOURCE_GROUP: 'rg', STATIC_WEB_APP_NAME: 'restockroo-testing', FAKE_SWA_HOST: 'gentle-sea-1.azurestaticapps.net', FAKE_SWA_TOKEN: 'swa-token-value' };
  const ok = runTestingStep(tokenScript, env);
  assert.equal(ok.status, 0, ok.log);
  assert.equal(ok.outputs.token, 'swa-token-value');
  // The only place the token appears in the log is the mask command, before any output uses it.
  assert.deepEqual(ok.log.split('\n').filter((line) => line.includes('swa-token-value')), ['::add-mask::swa-token-value']);

  const production = runTestingStep(tokenScript, { ...env, FAKE_SWA_HOST: 'Red-Island-0c128c000.7.azurestaticapps.net' });
  assert.notEqual(production.status, 0);
  assert.match(production.log, /production Static Web App/);
  assert.equal(production.outputs.token, undefined);
  assert.ok(!production.log.includes('swa-token-value'));

  const empty = runTestingStep(tokenScript, { ...env, FAKE_SWA_TOKEN: '' });
  assert.notEqual(empty.status, 0);
  const refused = runTestingStep(tokenScript, { ...env, FAKE_AZ_EXIT: '1' });
  assert.notEqual(refused.status, 0);
  assert.match(refused.log, /testing deployment identity/);
});
