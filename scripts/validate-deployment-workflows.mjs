// Production deployment workflow contract (issues #343 and #491).
//
// Proves from the workflow text that production changes only through the two manually started
// entry points, Deploy Production (no inputs) and Deploy Production (advanced recovery), and that
// both run the one shared release workflow: no push- or pull-request-triggered workflow can reach
// Azure, both entry points run only from main and share one non-cancelling concurrency group, the
// normal release deploys the dispatch commit and reads its baseline from the release records, every
// job is bound to the one resolved release SHA, validation, the migration preflight and the API
// health check gate what follows them, and each job keeps its expected environment and permission
// boundary.
//
// Usage: node scripts/validate-deployment-workflows.mjs

import { readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readRepositoryFile, repositoryRoot } from './validate-agent-workflows.mjs';
import { BASELINE_CONDITION, ISOLATION_CONDITION } from './validate-runner-placement.mjs';

export const deployProductionPath = '.github/workflows/deploy-production.yml';
export const deployProductionRecoveryPath = '.github/workflows/deploy-production-recovery.yml';
export const deployProductionReleasePath = '.github/workflows/deploy-production-release.yml';
export const RELEASE_WORKFLOW_CALL = 'uses: ./.github/workflows/deploy-production-release.yml';
// Both entry points declare exactly this block, so they share one queue and never cancel a deployment.
export const SHARED_CONCURRENCY = 'concurrency:\n  group: deploy-production\n  cancel-in-progress: false\n';
export const PRODUCTION_ENVIRONMENT = 'VmInventoryApi_Env';
export const RELEASE_SHA = '${{ needs.resolve.outputs.sha }}';
// Issues #645 and #807: in self-hosted mode credentialed jobs wait for a human to verify runner
// isolation, and the deploy jobs also wait for a human to verify the tool baseline on every runner,
// because the host that uploads the frontend is not known when the API is deployed. In GitHub-hosted
// mode neither variable describes the runner, so neither is required; each deploy job still checks
// its own tools before it uses a credential.
export const RUNNER_ISOLATION_CONDITION = ISOLATION_CONDITION;
export const RUNNER_BASELINE_CONDITION = BASELINE_CONDITION;
export const DEPLOY_JOB_CONDITION = `if: github.ref == 'refs/heads/main' && ${RUNNER_ISOLATION_CONDITION} && ${RUNNER_BASELINE_CONDITION}`;
const RUNNER_BASELINE_STEP = '- name: Require a verified tool baseline on every runner';

// Text that only a production deployment needs. No other workflow may contain any of it.
export const DEPLOYMENT_MARKERS = Object.freeze([
  'azure/login',
  'azure/webapps-deploy',
  'Azure/static-web-apps-deploy',
  'AZURE_STATIC_WEB_APPS_API_TOKEN',
  'VmInventoryApi_Env',
  'VmInventoryApi_CLIENT_ID',
]);

// A failed deployment must never change code, labels or start an agent; production recovery
// is a human decision.
const FORBIDDEN_IN_DEPLOY = Object.freeze([
  'git push',
  'gh pr ',
  'gh issue ',
  'gh label ',
  'gh workflow run',
  'gh run rerun',
  'client-secret',
  'creds:',
  'contents: write',
  'pull-requests: write',
  'issues: write',
  'actions: write',
  'cancel-in-progress: true',
]);

const compareStrings = (left, right) => {
  if (left === right) {
    return 0;
  }
  return left < right ? -1 : 1;
};

function fail(source, message) {
  throw new Error(`${source}: ${message}`);
}

function requireText(text, expected, source) {
  if (!text.includes(expected)) {
    fail(source, `missing required text: ${expected}`);
  }
}

function forbidText(text, forbidden, source) {
  if (text.includes(forbidden)) {
    fail(source, `contains forbidden text: ${forbidden}`);
  }
}

function requireOrder(text, first, second, source) {
  const firstIndex = text.indexOf(first);
  const secondIndex = text.indexOf(second);
  if (firstIndex < 0 || secondIndex < 0 || firstIndex > secondIndex) {
    fail(source, `'${first}' must come before '${second}'`);
  }
}

// Returns the top-level trigger names of a workflow (`on:` block, two-space keys).
export function extractTriggers(text) {
  const lines = text.split('\n');
  const start = lines.findIndex((line) => /^on:\s*$/.test(line));
  if (start < 0) {
    const inline = /^on:[ \t]*(\S.*)$/m.exec(text);
    if (!inline) {
      return [];
    }
    return inline[1].replace(/[[\]]/g, '').split(',').map((name) => name.trim()).filter(Boolean);
  }
  const triggers = [];
  for (const line of lines.slice(start + 1)) {
    if (/^\S/.test(line)) {
      break;
    }
    const match = /^ {2}([A-Za-z_]+):/.exec(line);
    if (match) {
      triggers.push(match[1]);
    }
  }
  return triggers;
}

// Splits the `jobs:` block into { jobId: jobText }.
export function extractJobs(text, source) {
  const lines = text.split('\n');
  const start = lines.findIndex((line) => /^jobs:\s*$/.test(line));
  if (start < 0) {
    fail(source, 'missing jobs block');
  }
  const jobs = {};
  let current = null;
  for (const line of lines.slice(start + 1)) {
    if (/^\S/.test(line)) {
      break;
    }
    const match = /^ {2}([A-Za-z0-9_-]+):\s*$/.exec(line);
    if (match) {
      current = match[1];
      jobs[current] = '';
      continue;
    }
    if (current) {
      jobs[current] += `${line}\n`;
    }
  }
  return jobs;
}

// Returns a job's permissions as { scope: access }, or null when the job declares none.
export function extractJobPermissions(jobText) {
  const lines = jobText.split('\n');
  const index = lines.findIndex((line) => /^ {4}permissions:/.test(line));
  if (index < 0) {
    return null;
  }
  if (/^ {4}permissions:\s*\{\}\s*$/.test(lines[index])) {
    return {};
  }
  const permissions = {};
  for (const line of lines.slice(index + 1)) {
    const match = /^ {6}([a-z-]+):\s*(\S+)\s*$/.exec(line);
    if (!match) {
      break;
    }
    permissions[match[1]] = match[2];
  }
  return permissions;
}

function extractNeeds(jobText) {
  const inline = /^ {4}needs:[ \t]*(\S.*)$/m.exec(jobText);
  if (inline) {
    return inline[1].replace(/[[\]]/g, '').split(',').map((name) => name.trim()).filter(Boolean);
  }
  const lines = jobText.split('\n');
  const index = lines.findIndex((line) => /^ {4}needs:\s*$/.test(line));
  if (index < 0) {
    return [];
  }
  const needs = [];
  for (const line of lines.slice(index + 1)) {
    const match = /^ {6}- ([A-Za-z0-9_-]+)\s*$/.exec(line);
    if (!match) {
      break;
    }
    needs.push(match[1]);
  }
  return needs;
}

function requirePermissions(jobs, jobId, expected, source) {
  const actual = extractJobPermissions(jobs[jobId]);
  if (actual === null) {
    fail(source, `job '${jobId}' must declare its own permissions`);
  }
  const format = (value) => JSON.stringify(Object.fromEntries(Object.entries(value).sort(([left], [right]) => compareStrings(left, right))));
  if (format(actual) !== format(expected)) {
    fail(source, `job '${jobId}' permissions must be exactly ${format(expected)}, found ${format(actual)}`);
  }
}

function requireNeeds(jobs, jobId, expected, source) {
  const needs = extractNeeds(jobs[jobId]);
  for (const dependency of expected) {
    if (!needs.includes(dependency)) {
      fail(source, `job '${jobId}' must need '${dependency}'`);
    }
  }
}

function requireJob(jobs, jobId, source) {
  if (!(jobId in jobs)) {
    fail(source, `missing job '${jobId}'`);
  }
  return jobs[jobId];
}

function forbidEnvironment(jobs, jobId, source) {
  if (/^ {4}environment:/m.test(jobs[jobId])) {
    fail(source, `job '${jobId}' must not use a deployment environment`);
  }
  forbidText(jobs[jobId], 'secrets.', `${source} job '${jobId}'`);
}

// Every workflow other than the shared release workflow must be free of deployment markers, so no
// push, pull request, schedule or agent event can reach Azure, and only the two entry points may
// call the release workflow.
export function verifyNoOtherWorkflowDeploys(workflows) {
  for (const [path, text] of Object.entries(workflows)) {
    if (path === deployProductionReleasePath) {
      continue;
    }
    for (const marker of DEPLOYMENT_MARKERS) {
      forbidText(text, marker, path);
    }
    if (path !== deployProductionPath && path !== deployProductionRecoveryPath) {
      forbidText(text, 'deploy-production-release.yml', path);
    }
  }
}

// Returns the text of the top-level `on:` block.
function extractOnBlock(text) {
  const lines = text.split('\n');
  const start = lines.findIndex((line) => /^on:/.test(line));
  if (start < 0) {
    return '';
  }
  const block = [lines[start]];
  for (const line of lines.slice(start + 1)) {
    if (/^\S/.test(line)) {
      break;
    }
    block.push(line);
  }
  return `${block.join('\n')}\n`;
}

const NORMAL_ENTRY = Object.freeze({
  name: 'Deploy Production',
  entryPoint: 'normal',
});
const RECOVERY_ENTRY = Object.freeze({
  name: 'Deploy Production (advanced recovery)',
  entryPoint: 'recovery',
});

// An entry point is a human-started dispatch from main with one gate job and one job that calls
// the shared release workflow; it holds no deployment step of its own.
function verifyEntryPointWorkflow(text, source, entry) {
  requireText(text, `name: ${entry.name}\n`, source);
  const triggers = extractTriggers(text);
  if (triggers.length !== 1 || triggers[0] !== 'workflow_dispatch') {
    fail(source, `must be triggered only by workflow_dispatch, found: ${triggers.join(', ') || 'none'}`);
  }
  if (!/^permissions: \{\}$/m.test(text)) {
    fail(source, 'top-level permissions must be {}');
  }
  if (!text.includes(`\n${SHARED_CONCURRENCY}`)) {
    fail(source, `must share the production concurrency group exactly: ${SHARED_CONCURRENCY.replaceAll('\n', ' ')}`);
  }
  for (const forbidden of FORBIDDEN_IN_DEPLOY) {
    forbidText(text, forbidden, source);
  }

  const jobs = extractJobs(text, source);
  const jobIds = Object.keys(jobs).sort(compareStrings);
  if (jobIds.join(',') !== 'gate,release') {
    fail(source, `must have exactly the jobs 'gate' and 'release', found: ${jobIds.join(', ')}`);
  }
  requireOrder(jobs.gate, "if: github.ref != 'refs/heads/main'", 'must be started from main; this run was started from $RUN_REF."\n          exit 1', `${source} job 'gate'`);
  requirePermissions(jobs, 'gate', {}, source);
  forbidEnvironment(jobs, 'gate', source);

  const release = jobs.release;
  requireText(release, `    ${RELEASE_WORKFLOW_CALL}\n`, `${source} job 'release'`);
  requireNeeds(jobs, 'release', ['gate'], source);
  requirePermissions(jobs, 'release', { contents: 'read', deployments: 'write', 'id-token': 'write' }, source);
  requireText(release, `      entry_point: ${entry.entryPoint}\n`, `${source} job 'release'`);
  requireText(release, '    secrets: inherit\n', `${source} job 'release'`);
  for (const forbidden of ['environment:', 'steps:', 'runs-on:']) {
    forbidText(release, forbidden, `${source} job 'release'`);
  }
  return { jobs, on: extractOnBlock(text) };
}

// Deploy Production, the normal release: no inputs at all, so the operator cannot type a commit or
// a baseline; the release workflow deploys the dispatch commit and reads the recorded baseline.
export function verifyDeployProductionWorkflow(text, source = deployProductionPath) {
  const { jobs, on } = verifyEntryPointWorkflow(text, source, NORMAL_ENTRY);
  if (on.includes('inputs:')) {
    fail(source, 'the normal Deploy Production release must expose no inputs (no sha or baseline_sha)');
  }
  for (const forbidden of ['inputs.', 'sha:', 'baseline_sha']) {
    forbidText(jobs.release, forbidden, `${source} job 'release'`);
  }
}

// Deploy Production (advanced recovery): an exact, required full SHA and an optional baseline that
// the release workflow accepts only when no release is recorded.
export function verifyDeployProductionRecoveryWorkflow(text, source = deployProductionRecoveryPath) {
  const { jobs, on } = verifyEntryPointWorkflow(text, source, RECOVERY_ENTRY);
  requireText(on, '      sha:\n        description: Full 40-character main commit SHA to deploy.\n        required: true\n        type: string\n', `${source} inputs`);
  requireText(on, '      baseline_sha:\n', `${source} inputs`);
  requireText(jobs.release, '      sha: ${{ inputs.sha }}\n', `${source} job 'release'`);
  requireText(jobs.release, '      baseline_sha: ${{ inputs.baseline_sha }}\n', `${source} job 'release'`);
}

// The release workflow both entry points call. It holds every deployment step.
export function verifyDeployProductionReleaseWorkflow(text, source = deployProductionReleasePath) {
  requireText(text, 'name: Deploy Production release\n', source);

  const triggers = extractTriggers(text);
  if (triggers.length !== 1 || triggers[0] !== 'workflow_call') {
    fail(source, `must be triggered only by workflow_call from the two entry points, found: ${triggers.join(', ') || 'none'}`);
  }
  if (!/^permissions: \{\}$/m.test(text)) {
    fail(source, 'top-level permissions must be {}');
  }
  // The concurrency group belongs to the callers, which share it; a second group here could not
  // protect the other entry point and could cancel its caller.
  forbidText(text, 'concurrency:', source);
  for (const forbidden of FORBIDDEN_IN_DEPLOY) {
    forbidText(text, forbidden, source);
  }
  const on = extractOnBlock(text);
  requireText(on, '      entry_point:\n', `${source} inputs`);

  const jobs = extractJobs(text, source);
  for (const jobId of ['resolve', 'validate', 'preflight', 'build', 'deploy-api', 'record-release', 'verify-api', 'deploy-frontend', 'summary']) {
    requireJob(jobs, jobId, source);
  }
  for (const jobId of Object.keys(jobs)) {
    if (extractJobPermissions(jobs[jobId]) === null) {
      fail(source, `job '${jobId}' must declare its own permissions`);
    }
  }

  // Ref gate: the first step of the first job refuses any ref other than main, before anything
  // else runs, and the SHA must be contained in main.
  const resolveJob = jobs.resolve;
  requireOrder(resolveJob, "if: github.ref != 'refs/heads/main'", 'uses: actions/checkout', `${source} job 'resolve'`);
  requireText(resolveJob, 'exit 1', `${source} job 'resolve'`);
  requireOrder(resolveJob, RUNNER_BASELINE_STEP, 'uses: actions/checkout', `${source} job 'resolve'`);
  requireText(resolveJob, 'if [ "$RUNNER_MODE" = "true" ] && [ "$RUNNER_BASELINE_VERIFIED" != "true" ]; then', `${source} job 'resolve'`);
  // GitHub-hosted mode (issue #807): no VM baseline applies, so the first job checks the deploy
  // jobs' tools on its own fresh runner before the API is validated, built or deployed.
  requireText(resolveJob, 'if [ "$RUNNER_MODE" != "true" ]; then', `${source} job 'resolve'`);
  requireText(resolveJob, 'for tool in az jq curl docker; do', `${source} job 'resolve'`);
  requireText(resolveJob, 'docker info >/dev/null 2>&1 ||', `${source} job 'resolve'`);
  requireText(resolveJob, 'merge-base --is-ancestor "$sha" refs/remotes/origin/main', `${source} job 'resolve'`);
  requireText(resolveJob, '^[0-9a-f]{40}$', `${source} job 'resolve'`);
  // The normal release deploys the commit captured at dispatch and refuses any supplied commit or
  // baseline; recovery requires its exact SHA; any other caller is refused.
  requireOrder(resolveJob, '            normal)\n', 'sha="$GITHUB_SHA"', `${source} job 'resolve'`);
  requireText(resolveJob, '[ -z "$INPUT_SHA" ] && [ -z "$INPUT_BASELINE" ]', `${source} job 'resolve'`);
  requireOrder(resolveJob, '            recovery)\n', '[ -n "$INPUT_SHA" ] || fail', `${source} job 'resolve'`);
  requireText(resolveJob, '*) fail "Unknown entry point', `${source} job 'resolve'`);
  // The baseline comes from the release records. A record that is not a main commit stops the run,
  // a supplied baseline that differs from a record is refused, an operator baseline is accepted
  // only by recovery when no record exists, and the release SHA is never used as the baseline.
  requireText(resolveJob, `task=$RELEASE_RECORD_TASK`, `${source} job 'resolve'`);
  requireText(resolveJob, '! git merge-base --is-ancestor "$recorded" refs/remotes/origin/main', `${source} job 'resolve'`);
  requireText(resolveJob, 'if [ -n "$INPUT_BASELINE" ] && [ "$INPUT_BASELINE" != "$recorded" ]; then', `${source} job 'resolve'`);
  requireText(resolveJob, 'elif [ "$ENTRY_POINT" = "recovery" ] && [ -n "$INPUT_BASELINE" ]; then', `${source} job 'resolve'`);
  requireText(resolveJob, 'merge-base --is-ancestor "$baseline" refs/remotes/origin/main', `${source} job 'resolve'`);
  for (const guessed of ['baseline="$sha"', 'baseline="$GITHUB_SHA"', 'baseline="${GITHUB_SHA}"', 'baseline_sha=$sha']) {
    forbidText(resolveJob, guessed, `${source} job 'resolve'`);
  }
  requirePermissions(jobs, 'resolve', { contents: 'read', deployments: 'read' }, source);
  forbidEnvironment(jobs, 'resolve', source);

  // Validation runs on the exact SHA with no credentials.
  requireText(jobs.validate, 'bash scripts/validate.sh', `${source} job 'validate'`);
  requireText(jobs.validate, `ref: ${RELEASE_SHA}`, `${source} job 'validate'`);
  requirePermissions(jobs, 'validate', { contents: 'read' }, source);
  forbidEnvironment(jobs, 'validate', source);

  requireText(jobs.preflight, 'node scripts/deployment-migration-preflight.mjs', `${source} job 'preflight'`);
  requireText(jobs.preflight, `ref: ${RELEASE_SHA}`, `${source} job 'preflight'`);
  requirePermissions(jobs, 'preflight', { contents: 'read' }, source);
  forbidEnvironment(jobs, 'preflight', source);

  // One build per artifact, from the release SHA, named by that SHA.
  requireText(jobs.build, `ref: ${RELEASE_SHA}`, `${source} job 'build'`);
  requireText(jobs.build, `name: api-${RELEASE_SHA}`, `${source} job 'build'`);
  requireText(jobs.build, `name: frontend-${RELEASE_SHA}`, `${source} job 'build'`);
  requirePermissions(jobs, 'build', { contents: 'read' }, source);
  forbidEnvironment(jobs, 'build', source);

  const deployApi = jobs['deploy-api'];
  requireText(deployApi, `${DEPLOY_JOB_CONDITION}\n`, `${source} job 'deploy-api'`);
  requireText(deployApi, `environment: ${PRODUCTION_ENVIRONMENT}`, `${source} job 'deploy-api'`);
  requireNeeds(jobs, 'deploy-api', ['resolve', 'validate', 'preflight', 'build'], source);
  requirePermissions(jobs, 'deploy-api', { contents: 'read', 'id-token': 'write' }, source);
  requireText(deployApi, `name: api-${RELEASE_SHA}`, `${source} job 'deploy-api'`);
  requireOrder(deployApi, 'needs.preflight.outputs.pending_count', 'uses: azure/login', `${source} job 'deploy-api'`);
  requireOrder(deployApi, '/compare/$RELEASE_SHA...main', 'uses: azure/login', `${source} job 'deploy-api'`);
  requireOrder(deployApi, 'uses: azure/login', 'uses: azure/webapps-deploy', `${source} job 'deploy-api'`);
  forbidText(deployApi, 'secrets.', `${source} job 'deploy-api'`);

  // The release becomes the next run's migration baseline, so it is recorded only once the API
  // passed its health check, never merely because the package upload succeeded.
  requirePermissions(jobs, 'record-release', { deployments: 'write' }, source);
  forbidEnvironment(jobs, 'record-release', source);
  requireNeeds(jobs, 'record-release', ['resolve', 'deploy-api', 'verify-api'], source);
  // Its only allowed condition is the isolation gate, which has no status function, so GitHub
  // still runs the job only when every job it needs succeeded.
  const recordConditions = jobs['record-release'].match(/^ {4}if:.*$/gm) ?? [];
  if (recordConditions.some((line) => line !== `    if: ${RUNNER_ISOLATION_CONDITION}`)) {
    fail(source, "job 'record-release' must run only when 'verify-api' succeeded (no if: other than the runner isolation gate)");
  }

  requireNeeds(jobs, 'verify-api', ['deploy-api'], source);
  requireText(jobs['verify-api'], '/health/ready', `${source} job 'verify-api'`);
  requirePermissions(jobs, 'verify-api', {}, source);
  forbidEnvironment(jobs, 'verify-api', source);
  if (/^ {4}if:/m.test(jobs['verify-api'])) {
    fail(source, "job 'verify-api' must run only when 'deploy-api' succeeded (no custom if:)");
  }

  const deployFrontend = jobs['deploy-frontend'];
  requireText(deployFrontend, `${DEPLOY_JOB_CONDITION}\n`, `${source} job 'deploy-frontend'`);
  requireText(deployFrontend, `environment: ${PRODUCTION_ENVIRONMENT}`, `${source} job 'deploy-frontend'`);
  requireNeeds(jobs, 'deploy-frontend', ['resolve', 'build', 'deploy-api', 'verify-api', 'record-release'], source);
  requirePermissions(jobs, 'deploy-frontend', { contents: 'read' }, source);
  requireText(deployFrontend, `name: frontend-${RELEASE_SHA}`, `${source} job 'deploy-frontend'`);
  requireText(deployFrontend, 'skip_app_build: true', `${source} job 'deploy-frontend'`);
  requireOrder(deployFrontend, '/compare/$RELEASE_SHA...main', 'uses: Azure/static-web-apps-deploy', `${source} job 'deploy-frontend'`);

  // Only the two deploy jobs hold a deployment environment or Azure access.
  for (const [jobId, jobText] of Object.entries(jobs)) {
    if (jobId === 'deploy-api' || jobId === 'deploy-frontend') {
      continue;
    }
    for (const marker of DEPLOYMENT_MARKERS) {
      forbidText(jobText, marker, `${source} job '${jobId}'`);
    }
    forbidText(jobText, 'id-token', `${source} job '${jobId}'`);
  }

  requireText(jobs.summary, 'if: always()', `${source} job 'summary'`);
  requireText(jobs.summary, 'ENTRY_POINT: ${{ inputs.entry_point }}', `${source} job 'summary'`);
  requireText(jobs.summary, '| Entry point |', `${source} job 'summary'`);
  requirePermissions(jobs, 'summary', {}, source);
}

export function readWorkflows(read = readRepositoryFile) {
  const directory = resolve(repositoryRoot, '.github/workflows');
  const workflows = {};
  for (const name of readdirSync(directory).sort(compareStrings)) {
    if (name.endsWith('.yml') || name.endsWith('.yaml')) {
      const path = `.github/workflows/${name}`;
      workflows[path] = read(path);
    }
  }
  return workflows;
}

export function verifyDeploymentWorkflows(workflows = readWorkflows()) {
  for (const path of [deployProductionPath, deployProductionRecoveryPath, deployProductionReleasePath]) {
    if (!(path in workflows)) {
      fail(path, 'missing');
    }
  }
  verifyNoOtherWorkflowDeploys(workflows);
  verifyDeployProductionWorkflow(workflows[deployProductionPath]);
  verifyDeployProductionRecoveryWorkflow(workflows[deployProductionRecoveryPath]);
  verifyDeployProductionReleaseWorkflow(workflows[deployProductionReleasePath]);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    verifyDeploymentWorkflows();
    console.log('Deployment workflow contract checks passed.');
  } catch (error) {
    console.error(error.message);
    process.exit(1);
  }
}
