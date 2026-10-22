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
  extractJobPermissions,
  extractJobs,
  extractTriggers,
  readWorkflows,
  verifyDeployProductionRecoveryWorkflow,
  verifyDeployProductionReleaseWorkflow,
  verifyDeployProductionWorkflow,
  verifyDeploymentWorkflows,
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
      assert.match(run.log, /mark that deployment record inactive/);
      assert.deepEqual(run.outputs, {});
    }
  }
  const unreadable = runStep(baselineScript, { entry: 'normal', ghExit: 1 });
  assert.equal(unreadable.status, 1);
  assert.match(unreadable.log, /Could not read the recorded production releases.*Re-run/);
  assert.deepEqual(unreadable.outputs, {});
});
