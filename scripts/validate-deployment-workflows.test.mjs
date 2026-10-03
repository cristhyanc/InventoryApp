import assert from 'node:assert/strict';
import test from 'node:test';
import { readRepositoryFile } from './validate-agent-workflows.mjs';
import {
  deployProductionPath,
  extractJobPermissions,
  extractJobs,
  extractTriggers,
  readWorkflows,
  verifyDeployProductionWorkflow,
  verifyDeploymentWorkflows,
  verifyNoOtherWorkflowDeploys,
} from './validate-deployment-workflows.mjs';

const deploy = readRepositoryFile(deployProductionPath);

function mutate(original, replacement) {
  assert.ok(deploy.includes(original), `fixture text not found: ${original}`);
  return deploy.replace(original, () => replacement);
}

function replaceInJob(jobId, original, replacement) {
  const jobs = extractJobs(deploy, deployProductionPath);
  const jobText = jobs[jobId].trimEnd();
  assert.ok(deploy.includes(jobText), `job text not found: ${jobId}`);
  assert.ok(jobText.includes(original), `fixture text not found in ${jobId}: ${original}`);
  return deploy.replace(jobText, () => jobText.replace(original, () => replacement));
}

test('the repository workflows satisfy the deployment contract', () => {
  verifyDeploymentWorkflows();
});

test('Deploy Production is triggered only by workflow_dispatch', () => {
  assert.deepEqual(extractTriggers(deploy), ['workflow_dispatch']);
});

test('pushes to main cannot reach Azure: a push trigger on Deploy Production is rejected', () => {
  const withPush = mutate('on:\n  workflow_dispatch:\n', 'on:\n  push:\n    branches:\n      - main\n  workflow_dispatch:\n');
  assert.throws(() => verifyDeployProductionWorkflow(withPush), /only by workflow_dispatch/);
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
  const withoutGate = mutate("if: github.ref != 'refs/heads/main'", "if: github.ref == 'refs/heads/never'");
  assert.throws(() => verifyDeployProductionWorkflow(withoutGate), /refs\/heads\/main/);

  const ungatedApi = replaceInJob('deploy-api', "if: github.ref == 'refs/heads/main'", 'if: true');
  assert.throws(() => verifyDeployProductionWorkflow(ungatedApi), /deploy-api/);

  const ungatedFrontend = replaceInJob('deploy-frontend', "if: github.ref == 'refs/heads/main'", 'if: true');
  assert.throws(() => verifyDeployProductionWorkflow(ungatedFrontend), /deploy-frontend/);
});

test('manual deployment rejects SHAs that are not contained in main', () => {
  const withoutAncestry = mutate('merge-base --is-ancestor "$sha" refs/remotes/origin/main', 'true');
  assert.throws(() => verifyDeployProductionWorkflow(withoutAncestry), /is-ancestor/);

  const withoutRecheck = replaceInJob('deploy-api', '/compare/$RELEASE_SHA...main', '/commits/$RELEASE_SHA');
  assert.throws(() => verifyDeployProductionWorkflow(withoutRecheck), /compare/);
});

test('backend and frontend deploy artifacts built from the same captured SHA', () => {
  const otherApiArtifact = replaceInJob('deploy-api', 'name: api-${{ needs.resolve.outputs.sha }}', 'name: api-${{ github.sha }}');
  assert.throws(() => verifyDeployProductionWorkflow(otherApiArtifact), /api-/);

  const otherFrontendArtifact = replaceInJob(
    'deploy-frontend',
    'name: frontend-${{ needs.resolve.outputs.sha }}',
    'name: frontend-${{ github.sha }}',
  );
  assert.throws(() => verifyDeployProductionWorkflow(otherFrontendArtifact), /frontend-/);

  const rebuildingFrontend = replaceInJob('deploy-frontend', 'skip_app_build: true', 'skip_app_build: false');
  assert.throws(() => verifyDeployProductionWorkflow(rebuildingFrontend), /skip_app_build/);

  const movingCheckout = replaceInJob('build', 'ref: ${{ needs.resolve.outputs.sha }}', 'ref: main');
  assert.throws(() => verifyDeployProductionWorkflow(movingCheckout), /job 'build'/);
});

test('failed validation, preflight or backend health prevents downstream deployment', () => {
  for (const dependency of ['validate', 'preflight', 'build']) {
    const ungated = replaceInJob('deploy-api', `      - ${dependency}\n`, '');
    assert.throws(() => verifyDeployProductionWorkflow(ungated), new RegExp(`must need '${dependency}'`));
  }

  const frontendWithoutHealth = replaceInJob('deploy-frontend', '      - verify-api\n', '');
  assert.throws(() => verifyDeployProductionWorkflow(frontendWithoutHealth), /must need 'verify-api'/);

  const healthAlways = replaceInJob('verify-api', '    runs-on: ubuntu-latest\n', '    if: always()\n    runs-on: ubuntu-latest\n');
  assert.throws(() => verifyDeployProductionWorkflow(healthAlways), /verify-api/);

  const noValidation = replaceInJob('validate', 'bash scripts/validate.sh', 'echo skipped');
  assert.throws(() => verifyDeployProductionWorkflow(noValidation), /validate\.sh/);
});

test('deploy jobs keep their environment and permission boundaries', () => {
  const jobs = extractJobs(deploy, deployProductionPath);
  assert.deepEqual(extractJobPermissions(jobs['deploy-api']), { contents: 'read', 'id-token': 'write' });
  assert.deepEqual(extractJobPermissions(jobs['deploy-frontend']), { contents: 'read' });
  assert.deepEqual(extractJobPermissions(jobs.validate), { contents: 'read' });
  assert.deepEqual(extractJobPermissions(jobs['verify-api']), {});

  const widened = replaceInJob('deploy-api', '      id-token: write\n', '      id-token: write\n      actions: write\n');
  assert.throws(() => verifyDeployProductionWorkflow(widened), /forbidden text: actions: write|permissions must be exactly/);

  const noEnvironment = replaceInJob('deploy-frontend', '    environment: VmInventoryApi_Env\n', '');
  assert.throws(() => verifyDeployProductionWorkflow(noEnvironment), /environment: VmInventoryApi_Env/);

  const credentialedValidation = replaceInJob('validate', '      contents: read\n', '      contents: read\n      id-token: write\n');
  assert.throws(() => verifyDeployProductionWorkflow(credentialedValidation), /validate/);

  const secretInValidation = replaceInJob(
    'validate',
    'run: bash scripts/validate.sh',
    'run: bash scripts/validate.sh\n        env:\n          TOKEN: ${{ secrets.AZURE_STATIC_WEB_APPS_API_TOKEN_RED_ISLAND_0C128C000 }}',
  );
  assert.throws(() => verifyDeployProductionWorkflow(secretInValidation), /validate/);
});

test('a failed deployment cannot push, relabel or start an agent', () => {
  for (const command of ['git push origin main', 'gh workflow run agent-repair.yml', 'gh pr merge 1', 'gh issue edit 1 --add-label agent-ready-claude']) {
    const tampered = replaceInJob('summary', 'echo "## Deploy Production"', `${command}\n            echo "## Deploy Production"`);
    assert.throws(() => verifyDeployProductionWorkflow(tampered), /forbidden text/);
  }
});

test('the production baseline is recorded only after the API is healthy', () => {
  const recordedOnUpload = replaceInJob('record-release', '      - verify-api\n', '');
  assert.throws(() => verifyDeployProductionWorkflow(recordedOnUpload), /record-release' must need 'verify-api'/);

  const recordedAlways = replaceInJob('record-release', '    runs-on: ubuntu-latest\n', '    if: always()\n    runs-on: ubuntu-latest\n');
  assert.throws(() => verifyDeployProductionWorkflow(recordedAlways), /record-release/);

  const frontendBeforeRecord = replaceInJob('deploy-frontend', '      - record-release\n', '');
  assert.throws(() => verifyDeployProductionWorkflow(frontendBeforeRecord), /must need 'record-release'/);
});
