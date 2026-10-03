// Production deployment workflow contract (issue #343).
//
// Proves from the workflow text that production changes only through the manually started
// Deploy Production workflow: no push- or pull-request-triggered workflow can reach Azure,
// Deploy Production runs only from main, every job is bound to the one resolved release SHA,
// validation, the migration preflight and the API health check gate what follows them, and each
// job keeps its expected environment and permission boundary.
//
// Usage: node scripts/validate-deployment-workflows.mjs

import { readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readRepositoryFile, repositoryRoot } from './validate-agent-workflows.mjs';

export const deployProductionPath = '.github/workflows/deploy-production.yml';
export const PRODUCTION_ENVIRONMENT = 'VmInventoryApi_Env';
export const RELEASE_SHA = '${{ needs.resolve.outputs.sha }}';

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
]);

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
  const format = (value) => JSON.stringify(Object.fromEntries(Object.entries(value).sort()));
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

// Every workflow other than Deploy Production must be free of deployment markers, so no push,
// pull request, schedule or agent event can reach Azure.
export function verifyNoOtherWorkflowDeploys(workflows) {
  for (const [path, text] of Object.entries(workflows)) {
    if (path === deployProductionPath) {
      continue;
    }
    for (const marker of DEPLOYMENT_MARKERS) {
      forbidText(text, marker, path);
    }
  }
}

export function verifyDeployProductionWorkflow(text, source = deployProductionPath) {
  requireText(text, 'name: Deploy Production\n', source);

  const triggers = extractTriggers(text);
  if (triggers.length !== 1 || triggers[0] !== 'workflow_dispatch') {
    fail(source, `must be triggered only by workflow_dispatch, found: ${triggers.join(', ') || 'none'}`);
  }
  if (!/^permissions: \{\}$/m.test(text)) {
    fail(source, 'top-level permissions must be {}');
  }
  requireText(text, 'cancel-in-progress: false', source);
  for (const forbidden of FORBIDDEN_IN_DEPLOY) {
    forbidText(text, forbidden, source);
  }

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
  requireText(resolveJob, 'merge-base --is-ancestor "$sha" refs/remotes/origin/main', `${source} job 'resolve'`);
  requireText(resolveJob, '^[0-9a-f]{40}$', `${source} job 'resolve'`);
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
  requireText(deployApi, "if: github.ref == 'refs/heads/main'", `${source} job 'deploy-api'`);
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
  if (/^ {4}if:/m.test(jobs['record-release'])) {
    fail(source, "job 'record-release' must run only when 'verify-api' succeeded (no custom if:)");
  }

  requireNeeds(jobs, 'verify-api', ['deploy-api'], source);
  requireText(jobs['verify-api'], '/health/ready', `${source} job 'verify-api'`);
  requirePermissions(jobs, 'verify-api', {}, source);
  forbidEnvironment(jobs, 'verify-api', source);
  if (/^ {4}if:/m.test(jobs['verify-api'])) {
    fail(source, "job 'verify-api' must run only when 'deploy-api' succeeded (no custom if:)");
  }

  const deployFrontend = jobs['deploy-frontend'];
  requireText(deployFrontend, "if: github.ref == 'refs/heads/main'", `${source} job 'deploy-frontend'`);
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
  requirePermissions(jobs, 'summary', {}, source);
}

export function readWorkflows(read = readRepositoryFile) {
  const directory = resolve(repositoryRoot, '.github/workflows');
  const workflows = {};
  for (const name of readdirSync(directory).sort()) {
    if (name.endsWith('.yml') || name.endsWith('.yaml')) {
      const path = `.github/workflows/${name}`;
      workflows[path] = read(path);
    }
  }
  return workflows;
}

export function verifyDeploymentWorkflows(workflows = readWorkflows()) {
  if (!(deployProductionPath in workflows)) {
    fail(deployProductionPath, 'missing');
  }
  verifyNoOtherWorkflowDeploys(workflows);
  verifyDeployProductionWorkflow(workflows[deployProductionPath]);
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
